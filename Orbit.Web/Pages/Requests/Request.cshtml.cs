using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Requests;

/// <summary>
/// A request's page (spec §6.20): its steps as a timeline, and what the viewer may do - fill in a form, open a page, approve or decline,
/// retry or skip a failed step, cancel the request.
/// </summary>
public class RequestModel(RequestService requests, IActorProvider actors) : OrbitPageModel
{
    public Request Item { get; private set; } = null!;
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyDictionary<string, string?> Values { get; private set; } = new Dictionary<string, string?>();
    public bool CanCancel { get; private set; }
    public bool CanManage { get; private set; }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Item = await requests.GetAsync(id, ct);
        Values = RequestEngine.BuildValues(Item);
        CanCancel = AccessPolicy.CanCancelRequest(Actor, Item);
        CanManage = AccessPolicy.CanManageRequestsIn(Actor, Item.DepartmentId);
    }

    public async Task<IActionResult> OnPostDecideAsync(Guid id, Guid approvalId, bool approve, string? comment, CancellationToken ct)
    {
        try
        {
            await requests.DecideAsync(approvalId, approve, comment, ct);
            Success(approve ? "Approved." : "Declined.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUrlDoneAsync(Guid id, Guid stepId, CancellationToken ct)
    {
        try
        {
            await requests.MarkUrlDoneAsync(stepId, ct);
            Success("Marked as done.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await requests.CancelAsync(id, ct);
            Success("Request cancelled.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, Guid stepId, CancellationToken ct)
    {
        try
        {
            await requests.RetryAsync(stepId, ct);
            Success("Step retried.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSkipAsync(Guid id, Guid stepId, CancellationToken ct)
    {
        try
        {
            await requests.SkipAsync(stepId, ct);
            Success("Step skipped.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    /// <summary>A web-page step's address with the request's values filled in.</summary>
    public string UrlFor(RequestStep step) => RequestTokenRules.RenderUrl(step.FlowStep.Url, Values);

    /// <summary>The viewer's open decision in the step's current stage, if the step is waiting on them.</summary>
    public RequestApproval? MyApproval(RequestStep step)
    {
        if (step.Status != RequestStepStatus.Ready || step.Approvals.Count == 0) return null;
        var current = step.Approvals.Max(a => a.StageOrder);
        return step.Approvals.FirstOrDefault(a => a.StageOrder == current && a.ApproverId == Actor.UserId && a.Decision == ApprovalDecision.Pending);
    }

    public bool CanAct(RequestStep step) => step.Status == RequestStepStatus.Ready && AccessPolicy.CanActOnStep(Actor, Item, step);
}

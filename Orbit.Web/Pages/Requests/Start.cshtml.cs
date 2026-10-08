using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Requests;

/// <summary>
/// Logging a request through a flow (spec §6.20): the start form's fields one at a time (requests.js), a review, then the request is
/// logged and its page opens. Refused answers come back beside their fields, the answers kept. A flow with no start form just confirms.
/// </summary>
public class StartModel(RequestService requests, IOptions<AttachmentOptions> attachmentOptions) : OrbitPageModel
{
    public RequestFlow Flow { get; private set; } = null!;
    public RequestFlowStep? StartForm { get; private set; }
    public AttachmentOptions AttachmentLimits => attachmentOptions.Value;

    [BindProperty] public Dictionary<Guid, AnswerForm> Answers { get; set; } = new();
    /// <summary>Set once when the page is first shown, so a double click or a resubmitted page logs the request once.</summary>
    [BindProperty] public string? IdempotencyKey { get; set; }

    public IReadOnlyDictionary<Guid, string> Errors { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> Labels { get; private set; } = new Dictionary<Guid, string>();
    /// <summary>What each Asset type field offers, for its list.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>> AssetTypes { get; private set; } = new Dictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>>();
    /// <summary>The assets each Asset field lists as buttons; a field that isn't here searches.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<RequestLookupItem>> AssetChoices { get; private set; } = new Dictionary<Guid, IReadOnlyList<RequestLookupItem>>();
    public string? GeneralError { get; private set; }
    public bool FilesDropped { get; private set; }

    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        if (context.HandlerMethod?.MethodInfo.Name == nameof(OnPostAsync)) Uploads.AllowUploadBody(HttpContext, AttachmentLimits);
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        IdempotencyKey = Guid.NewGuid().ToString("N");
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        var answers = RequestFormPosts.Answers(Answers);
        var files = RequestFormPosts.Files(Request.Form.Files, StartForm?.Fields ?? []);
        try
        {
            var request = await requests.StartAsync(id, answers, files, IdempotencyKey, ct);
            Success($"Request {request.Number} logged with {Flow.Category.Department.Name}.");
            return RedirectToPage("/Requests/Request", new { id = request.Id });
        }
        catch (RequestAnswersException ex)
        {
            Errors = ex.Errors;
        }
        catch (ValidationException ex)
        {
            GeneralError = ex.Message;
        }
        FilesDropped = files.Values.Any(f => f.Count > 0);
        Labels = await LabelsAsync(ct);
        return Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Flow = await requests.FlowAsync(id, ct);
        StartForm = RequestFlowRules.StartForm(Flow);
        if (StartForm is null) return;
        AssetTypes = await requests.AssetTypeChoicesAsync(Flow, StartForm, ct);
        AssetChoices = await requests.AssetChoicesAsync(Flow, StartForm, ct);
    }

    /// <summary>After a refused post the pickers only have ids; their labels are looked up again so the chips show.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(CancellationToken ct)
    {
        var labels = new Dictionary<Guid, string>();
        foreach (var (fieldId, answer) in Answers)
            if (answer.Id is Guid picked && await requests.PickLabelAsync(fieldId, picked, ct) is { } label) labels[fieldId] = label;
        return labels;
    }
}

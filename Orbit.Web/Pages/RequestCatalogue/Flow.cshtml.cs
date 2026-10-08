using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>One request flow's fields and its steps in order (spec §6.20); each step has its own page.</summary>
public class FlowModel(RequestCatalogueService catalogue, IActorProvider actors) : OrbitPageModel
{
    public RequestFlow Flow { get; private set; } = null!;
    public int InProgress { get; private set; }
    public bool CanSubmit { get; private set; }
    public FlowForm Form { get; private set; } = new();
    public string? SaveError { get; private set; }
    public StepAddForm NewStep { get; private set; } = new();
    public string? AddError { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        CanSubmit = AccessPolicy.CanSubmitRequests(await actors.GetAsync(HttpContext.RequestAborted));
        await next();
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = FlowForm.From(Flow);
    }

    public async Task<IActionResult> OnPostAsync(Guid id, FlowForm form, CancellationToken ct)
    {
        try
        {
            var saved = await catalogue.UpdateFlowAsync(id, form.ToInput(), ct);
            Success($"Flow \"{saved.Title}\" saved.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await LoadAsync(id, ct);
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        await catalogue.SetFlowArchivedAsync(id, archived, ct);
        Success(archived ? "Flow archived: no longer offered. Requests in progress carry on." : "Flow restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var flow = await catalogue.GetFlowAsync(id, ct);
        try
        {
            var categoryId = await catalogue.DeleteFlowAsync(id, ct);
            Success($"Flow \"{flow.Title}\" deleted.");
            return RedirectToPage("/RequestCatalogue/Category", new { id = categoryId });
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    public async Task<IActionResult> OnPostAddStepAsync(Guid id, StepAddForm step, CancellationToken ct)
    {
        try
        {
            var added = await catalogue.AddStepAsync(id, step.ToInput(), ct);
            Success($"Step \"{added.Title}\" added. Now set it up.");
            return RedirectToPage("/RequestCatalogue/Step", new { id = added.Id });
        }
        catch (ValidationException ex) { AddError = ex.Message; }
        await LoadAsync(id, ct);
        Form = FlowForm.From(Flow);
        NewStep = step;
        return Page();
    }

    public async Task<IActionResult> OnPostMoveStepAsync(Guid id, Guid stepId, int direction, CancellationToken ct)
    {
        await catalogue.MoveStepAsync(stepId, direction, ct);
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Flow = await catalogue.GetFlowAsync(id, ct);
        InProgress = await catalogue.InProgressRequestCountAsync(id, ct);
    }
}

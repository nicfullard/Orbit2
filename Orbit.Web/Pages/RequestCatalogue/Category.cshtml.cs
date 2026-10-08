using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>One request category's fields and its flows (spec §6.20).</summary>
public class CategoryModel(RequestCatalogueService catalogue, IActorProvider actors) : OrbitPageModel
{
    public RequestCategory Category { get; private set; } = null!;
    /// <summary>Whether the link to the category on the Requests page (requests.submit) would open.</summary>
    public bool CanSubmit { get; private set; }
    public CategoryForm Form { get; private set; } = new();
    public string? SaveError { get; private set; }
    public FlowForm NewFlow { get; private set; } = new();
    public string? AddError { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        CanSubmit = AccessPolicy.CanSubmitRequests(await actors.GetAsync(HttpContext.RequestAborted));
        await next();
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Category = await catalogue.GetCategoryAsync(id, ct);
        Form = CategoryForm.From(Category);
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CategoryForm form, CancellationToken ct)
    {
        try
        {
            var saved = await catalogue.UpdateCategoryAsync(id, form.ToInput(), ct);
            Success($"Category \"{saved.Title}\" saved.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        Category = await catalogue.GetCategoryAsync(id, ct);
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        await catalogue.SetCategoryArchivedAsync(id, archived, ct);
        Success(archived ? "Category archived: it and its flows are no longer offered. Nothing is deleted." : "Category restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var category = await catalogue.GetCategoryAsync(id, ct);
        try
        {
            var flows = await catalogue.DeleteCategoryAsync(id, ct);
            Success(flows == 0 ? $"Category \"{category.Title}\" deleted." : $"Category \"{category.Title}\" deleted, with its {flows} {(flows == 1 ? "flow" : "flows")}.");
            return RedirectToPage("/RequestCatalogue/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    public async Task<IActionResult> OnPostAddFlowAsync(Guid id, FlowForm flow, CancellationToken ct)
    {
        try
        {
            var added = await catalogue.AddFlowAsync(id, flow.ToInput(), ct);
            Success($"Flow \"{added.Title}\" added. Now add its steps: it is offered once every step is set up.");
            return RedirectToPage("/RequestCatalogue/Flow", new { id = added.Id });
        }
        catch (ValidationException ex) { AddError = ex.Message; }
        Category = await catalogue.GetCategoryAsync(id, ct);
        Form = CategoryForm.From(Category);
        NewFlow = flow;
        return Page();
    }

    public async Task<IActionResult> OnPostMoveFlowAsync(Guid id, Guid flowId, int direction, CancellationToken ct)
    {
        await catalogue.MoveFlowAsync(flowId, direction, ct);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveFlowAsync(Guid id, Guid flowId, bool archived, CancellationToken ct)
    {
        await catalogue.SetFlowArchivedAsync(flowId, archived, ct);
        Success(archived ? "Flow archived: no longer offered. Requests in progress carry on." : "Flow restored.");
        return RedirectToPage(new { id });
    }
}

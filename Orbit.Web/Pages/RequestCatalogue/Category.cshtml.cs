using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>One request category's fields and its options (spec §6.20).</summary>
public class CategoryModel(RequestCatalogueService catalogue, IActorProvider actors) : OrbitPageModel
{
    public RequestCategory Category { get; private set; } = null!;
    /// <summary>Whether the link to the category on the Requests page (requests.submit) would open.</summary>
    public bool CanSubmit { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        CanSubmit = AccessPolicy.CanSubmitRequests(await actors.GetAsync(HttpContext.RequestAborted));
        await next();
    }
    public CategoryForm Form { get; private set; } = new();
    public string? SaveError { get; private set; }
    public OptionForm NewOption { get; private set; } = OptionForm.New();
    public string? AddError { get; private set; }

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
        Success(archived ? "Category archived: it and its options are no longer offered. Nothing is deleted." : "Category restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var category = await catalogue.GetCategoryAsync(id, ct);
        var options = await catalogue.DeleteCategoryAsync(id, ct);
        Success(options == 0 ? $"Category \"{category.Title}\" deleted." : $"Category \"{category.Title}\" deleted, with its {options} {(options == 1 ? "option" : "options")}.");
        return RedirectToPage("/RequestCatalogue/Index");
    }

    public async Task<IActionResult> OnPostAddOptionAsync(Guid id, OptionForm option, CancellationToken ct)
    {
        try
        {
            var added = await catalogue.AddOptionAsync(id, option.ToInput(), ct);
            if (added.Kind == RequestOptionKind.Flow)
            {
                Success($"Option \"{added.Title}\" added. Add its questions: it is offered once it has one.");
                return RedirectToPage("/RequestCatalogue/Option", new { id = added.Id });
            }
            Success($"Link \"{added.Title}\" added.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { AddError = ex.Message; }
        Category = await catalogue.GetCategoryAsync(id, ct);
        Form = CategoryForm.From(Category);
        NewOption = option;
        return Page();
    }

    public async Task<IActionResult> OnPostMoveOptionAsync(Guid id, Guid optionId, int direction, CancellationToken ct)
    {
        await catalogue.MoveOptionAsync(optionId, direction, ct);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveOptionAsync(Guid id, Guid optionId, bool archived, CancellationToken ct)
    {
        await catalogue.SetOptionArchivedAsync(optionId, archived, ct);
        Success(archived ? "Option archived: no longer offered." : "Option restored.");
        return RedirectToPage(new { id });
    }
}

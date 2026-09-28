using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>
/// Configuring request flows (spec §6.20): the categories within the caller's requests.configure reach, by department, a form to add
/// one, and the example catalogue for a department that has none.
/// </summary>
public class IndexModel(RequestCatalogueService catalogue, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<RequestCategory> Categories { get; private set; } = [];
    /// <summary>The open departments the caller configures: their own at Department scope, every one at All.</summary>
    public IReadOnlyList<Department> Departments { get; private set; } = [];
    /// <summary>Departments in reach with no categories at all: the ones the example can go into.</summary>
    public IReadOnlyList<Department> EmptyDepartments { get; private set; } = [];
    public bool Everywhere { get; private set; }
    public CategoryForm NewCategory { get; private set; } = new();
    public string? AddError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(CategoryForm category, CancellationToken ct)
    {
        try
        {
            var added = await catalogue.CreateCategoryAsync(category.ToInput(), ct);
            Success($"Category \"{added.Title}\" added. Now add its options.");
            return RedirectToPage("/RequestCatalogue/Category", new { id = added.Id });
        }
        catch (ValidationException ex)
        {
            AddError = ex.Message;
        }
        await LoadAsync(ct);
        NewCategory = category;
        return Page();
    }

    public async Task<IActionResult> OnPostExampleAsync(Guid departmentId, CancellationToken ct)
    {
        try
        {
            var count = await catalogue.LoadExampleAsync(departmentId, ct);
            Success($"Loaded the example: {count} categories, ready to change. Its link under \"Help and self-service\" goes to {RequestExample.PlaceholderUrl} until you replace it.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid id, int direction, CancellationToken ct)
    {
        await catalogue.MoveCategoryAsync(id, direction, ct);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Everywhere = Actor.CanAnywhere(Permission.RequestsConfigure);
        Categories = await catalogue.ListAsync(ct);
        Departments = Everywhere
            ? await departments.ListAsync(false, ct)
            : Actor.DepartmentId is Guid own && Actor.ScopeOf(Permission.RequestsConfigure) == PermissionScope.Department
                ? [await departments.GetAsync(own, ct)]
                : [];
        Departments = Departments.Where(d => !d.IsArchived).ToList();
        var used = Categories.Select(c => c.DepartmentId).ToHashSet();
        EmptyDepartments = Departments.Where(d => !used.Contains(d.Id)).ToList();
        NewCategory = new CategoryForm { DepartmentId = Actor.DepartmentId ?? Departments.FirstOrDefault()?.Id };
    }
}

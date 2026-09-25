using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Users;

/// <summary>
/// Import from directory (spec §6.13): list the directory's users through an agent, tick people, choose a department
/// for each directory department Orbit couldn't link, pick one role, import. Load and Import both post and re-render,
/// like Test connection on the Directory page: the list is never kept between requests.
/// </summary>
// A take-on ticks hundreds of people, each one a form value; the default limit is 1,024 values.
[RequestFormLimits(ValueCountLimit = 20_000)]
public class ImportModel(DirectoryImportService import, DepartmentService departments, RoleService roles) : OrbitPageModel
{
    [BindProperty] public DirectoryImportQuery Query { get; set; } = new();
    [BindProperty] public List<string> Selected { get; set; } = [];
    [BindProperty] public Guid RoleId { get; set; }
    [BindProperty] public List<MappingInput> Map { get; set; } = [];

    public DirectoryImportSetup Setup { get; private set; } = default!;
    public DirectoryImportPreview? Preview { get; private set; }
    public DirectoryImportOutcome? Outcome { get; private set; }
    public IReadOnlyList<RolePickerItem> RoleItems { get; private set; } = [];
    public IReadOnlyList<Department> Departments { get; private set; } = [];

    public HashSet<string> SelectedKeys => Selected.ToHashSet(StringComparer.Ordinal);

    /// <summary>The department chosen for an unlinked directory value, kept when the page re-renders after a refusal.</summary>
    public Guid? MappedTo(string key) => Map.FirstOrDefault(m => (m.Key ?? string.Empty) == key)?.DepartmentId;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Query = Setup.Defaults;
        RoleId = UserForm.DefaultRole(RoleItems);
    }

    public async Task<IActionResult> OnPostLoadAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Selected = [];
        Map = [];
        await PreviewAsync(ct);
        if (RoleId == Guid.Empty) RoleId = UserForm.DefaultRole(RoleItems);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        try
        {
            Outcome = await import.ImportAsync(new DirectoryImportRequest
            {
                Query = Query,
                Selected = Selected,
                RoleId = RoleId,
                Mappings = Map.Select(m => new DirectoryImportMapping(m.Key ?? string.Empty, m.DepartmentId)).ToList()
            }, ct);
            Selected = [];
            Map = [];
        }
        catch (ValidationException ex)
        {
            // Nothing was created: show the list again with the admin's ticks and choices, so they can fix what was refused.
            ModelState.AddModelError(string.Empty, ex.Message);
            await PreviewAsync(ct);
        }
        return Page();
    }

    private async Task PreviewAsync(CancellationToken ct)
    {
        try
        {
            Preview = await import.PreviewAsync(Query, ct);
            Query = Preview.Query;
        }
        catch (ValidationException ex)
        {
            // After a refused import the listing may fail for the same reason (say, the agent went away); say it once.
            if (ModelState[string.Empty]?.Errors.Any(e => e.ErrorMessage == ex.Message) != true)
                ModelState.AddModelError(string.Empty, ex.Message);
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Setup = await import.GetSetupAsync(ct);
        RoleItems = await roles.ListForPickerAsync(ct);
        Departments = await departments.ListAsync(false, ct);
    }
}

public sealed class MappingInput
{
    /// <summary>The directory value's comparison key; empty is "no department in the directory".</summary>
    public string? Key { get; set; }
    public Guid? DepartmentId { get; set; }
}

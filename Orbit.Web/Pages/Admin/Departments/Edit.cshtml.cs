using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Departments;

public class EditModel(DepartmentService departments, UserDirectoryService users) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [BindProperty, StringLength(2000)] public string? Description { get; set; }
    [BindProperty] public Guid? ManagerId { get; set; }
    public Department Department { get; private set; } = null!;
    /// <summary>The manager chosen, as the picker's chip.</summary>
    public IReadOnlyList<UserSummary> Manager { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Department = await departments.GetAsync(id, ct);
        Name = Department.Name;
        Description = Department.Description;
        ManagerId = Department.ManagerId;
        await LoadManagerAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Department = await departments.GetAsync(id, ct);
        if (ModelState.IsValid)
        {
            try
            {
                var dept = await departments.UpdateAsync(id, Name, Description, ManagerId, ct);
                Success($"Department \"{dept.Name}\" saved.");
                return RedirectToPage("/Admin/Departments/Details", new { id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        await LoadManagerAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        try
        {
            await departments.SetArchivedAsync(id, archived, ct);
            Success(archived ? "Department archived. Existing users, projects and tasks are untouched." : "Department restored.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    private async Task LoadManagerAsync(CancellationToken ct) =>
        Manager = ManagerId is Guid m ? await users.FindManyAsync([m], ct) : [];
}

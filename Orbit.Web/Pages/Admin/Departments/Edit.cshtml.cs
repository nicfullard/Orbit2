using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Departments;

public class EditModel(DepartmentService departments) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [BindProperty, StringLength(2000)] public string? Description { get; set; }
    public Department Department { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Department = await departments.GetAsync(id, ct);
        Name = Department.Name;
        Description = Department.Description;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Department = await departments.GetAsync(id, ct);
        if (!ModelState.IsValid) return Page();
        try
        {
            var dept = await departments.UpdateAsync(id, Name, Description, ct);
            Success($"Department \"{dept.Name}\" saved.");
            return RedirectToPage("/Admin/Departments/Details", new { id });
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
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
}

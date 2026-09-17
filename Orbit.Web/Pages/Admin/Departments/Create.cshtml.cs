using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Departments;

public class CreateModel(DepartmentService departments) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [BindProperty, StringLength(2000)] public string? Description { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var dept = await departments.CreateAsync(Name, Description, ct);
            Success($"Department \"{dept.Name}\" created.");
            return RedirectToPage("/Admin/Departments/Details", new { id = dept.Id });
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}

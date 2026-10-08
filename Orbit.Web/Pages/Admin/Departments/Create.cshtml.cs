using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Departments;

public class CreateModel(DepartmentService departments, UserDirectoryService users) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [BindProperty, StringLength(2000)] public string? Description { get; set; }
    [BindProperty] public Guid? ManagerId { get; set; }
    /// <summary>The manager chosen, as the picker's chip, when the form comes back.</summary>
    public IReadOnlyList<UserSummary> Manager { get; private set; } = [];

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var dept = await departments.CreateAsync(Name, Description, ManagerId, ct);
                Success($"Department \"{dept.Name}\" created.");
                return RedirectToPage("/Admin/Departments/Details", new { id = dept.Id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Manager = ManagerId is Guid m ? await users.FindManyAsync([m], ct) : [];
        return Page();
    }
}

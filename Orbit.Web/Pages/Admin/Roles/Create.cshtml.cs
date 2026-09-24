using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Roles;

public class CreateModel(RoleService roles) : OrbitPageModel
{
    [BindProperty] public RoleForm Form { get; set; } = RoleForm.Empty();

    public void OnGet()
    {
        Form = RoleForm.Empty();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var role = await roles.CreateAsync(Form.ToInput(), ct);
                Success($"Role \"{role.Name}\" created.");
                return RedirectToPage("/Admin/Roles/Edit", new { id = role.Id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        Form.Normalize();
        return Page();
    }
}

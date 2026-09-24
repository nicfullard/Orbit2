using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Roles;

public class EditModel(RoleService roles) : OrbitPageModel
{
    [BindProperty] public RoleForm Form { get; set; } = new();
    public RoleListItem Role { get; private set; } = null!;
    public bool InUse => Role.UserCount > 0 || Role.KeyCount > 0;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Role = await roles.GetAsync(id, ct);
        Form = RoleForm.From(Role);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Role = await roles.GetAsync(id, ct);
        if (ModelState.IsValid)
        {
            try
            {
                var role = await roles.UpdateAsync(id, Form.ToInput(), ct);
                Success($"Role \"{role.Name}\" saved. It applies to everyone in it from their next request.");
                return RedirectToPage(new { id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        Form.Normalize();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await roles.DeleteAsync(id, ct);
            Success("Role deleted.");
            return RedirectToPage("/Admin/Roles/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }
}

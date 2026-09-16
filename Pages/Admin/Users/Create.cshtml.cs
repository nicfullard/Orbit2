using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Users;

public class CreateModel(UserAdminService users, DepartmentService departments) : OrbitPageModel
{
    [BindProperty] public UserForm Form { get; set; } = new();
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        DepartmentItems = await UserForm.DepartmentItemsAsync(departments, Form.DepartmentId, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var user = await users.CreateAsync(Form.ToInput(), ct);
                Success($"User {user.DisplayName} created. Hand them the temporary password; they can change it under Manage account.");
                return RedirectToPage("/Admin/Users/Edit", new { id = user.Id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        DepartmentItems = await UserForm.DepartmentItemsAsync(departments, Form.DepartmentId, ct);
        return Page();
    }
}

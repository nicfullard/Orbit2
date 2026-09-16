using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Users;

public class EditModel(UserAdminService users, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public UserForm Form { get; set; } = new();
    [BindProperty] public string? TemporaryPassword { get; set; }
    public UserSummary Account { get; private set; } = null!;
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public string? ResetLink { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = UserForm.From(Account);
        DepartmentItems = await UserForm.DepartmentItemsAsync(departments, Form.DepartmentId, ct);
        ResetLink = TempData["ResetLink"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        ModelState.Remove("Form.Email");
        ModelState.Remove("Form.Password");
        if (ModelState.IsValid)
        {
            try
            {
                var updated = await users.UpdateAsync(id, Form.ToInput(), ct);
                Success($"{updated.DisplayName} saved.");
                return RedirectToPage(new { id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        DepartmentItems = await UserForm.DepartmentItemsAsync(departments, Form.DepartmentId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await users.DeactivateAsync(id, ct);
            Success("User deactivated. They can no longer sign in; their history is kept.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReactivateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await users.ReactivateAsync(id, ct);
            Success("User reactivated.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResetLinkAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var (user, token) = await users.GeneratePasswordResetTokenAsync(id, ct);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            TempData["ResetLink"] = Url.Page("/Account/ResetPassword", pageHandler: null,
                values: new { area = "Identity", code }, protocol: Request.Scheme);
            Success($"Reset link generated for {user.DisplayName}. Hand it to them; it expires in a day.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSetPasswordAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await users.SetTemporaryPasswordAsync(id, TemporaryPassword ?? string.Empty, ct);
            Success("Temporary password set. Existing sessions for this user are signed out.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Account = await users.GetAsync(id, ct);
    }
}

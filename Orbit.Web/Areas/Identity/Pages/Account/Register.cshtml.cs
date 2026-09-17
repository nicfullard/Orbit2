using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Orbit.Areas.Identity.Pages.Account;

/// <summary>
/// Overrides the default Identity UI Register page: self-registration is disabled.
/// Accounts are created by a System Admin under Admin > Users.
/// </summary>
[AllowAnonymous]
public class RegisterModel : PageModel
{
    public IActionResult OnGet() => Page();

    public IActionResult OnPost() => NotFound();
}

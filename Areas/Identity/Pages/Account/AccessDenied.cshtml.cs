using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Orbit.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public string? Reason { get; private set; }

    public void OnGet()
    {
        Reason = TempData["Error"] as string;
    }
}

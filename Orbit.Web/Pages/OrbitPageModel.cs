using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Orbit.Pages;

public abstract class OrbitPageModel : PageModel
{
    protected void Success(string message) => TempData["Success"] = message;
    protected void Error(string message) => TempData["Error"] = message;

    protected string CurrentUrl => Request.Path + Request.QueryString;

    protected string SafeReturnUrl(string? returnUrl, string fallback) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : fallback;
}

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Orbit.Areas.Identity.Pages.Account.Manage;

/// <summary>Overrides the default Identity UI page: account deletion is handled by a System Admin (soft deactivation), never as a hard delete.</summary>
public class PersonalDataModel : PageModel
{
    public void OnGet() { }
}

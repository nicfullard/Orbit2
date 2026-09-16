using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Orbit.Areas.Identity.Pages.Account.Manage;

/// <summary>Overrides the default Identity UI page so users cannot hard-delete their own account.</summary>
public class DeletePersonalDataModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("./PersonalData");

    public IActionResult OnPost() => NotFound();
}

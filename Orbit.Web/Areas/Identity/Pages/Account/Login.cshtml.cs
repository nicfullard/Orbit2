using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orbit.Auth;
using Orbit.Data.Entities;

namespace Orbit.Areas.Identity.Pages.Account;

/// <summary>
/// Overrides the default Identity UI Login page. One form serves both kinds of user: whether the password is checked
/// against Orbit's own hash or the company directory is decided per user inside <see cref="OrbitSignInManager"/>
/// (spec §6.13), so this page only has to deal with the one extra outcome - the directory being unreachable.
/// </summary>
[AllowAnonymous]
public class LoginModel(SignInManager<ApplicationUser> signInManager, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string ReturnUrl { get; private set; } = "/";

    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        [Display(Name = "Remember me?")] public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
        if (!ModelState.IsValid) return Page();

        // lockoutOnFailure: repeated wrong passwords lock the account for a while. For directory users this also
        // stops Orbit being used to guess Active Directory passwords from the internet.
        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            logger.LogInformation("User logged in.");
            return LocalRedirect(ReturnUrl);
        }
        if (result.RequiresTwoFactor)
            return RedirectToPage("./LoginWith2fa", new { ReturnUrl, Input.RememberMe });
        if (result.IsLockedOut)
        {
            logger.LogWarning("User account locked out.");
            return RedirectToPage("./Lockout");
        }

        ModelState.AddModelError(string.Empty, result is DirectorySignInResult
            ? "Sign-in with your company directory account is temporarily unavailable. Please try again in a few minutes, or contact your administrator."
            : "Invalid login attempt.");
        return Page();
    }
}

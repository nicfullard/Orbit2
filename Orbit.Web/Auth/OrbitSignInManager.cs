using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>Returned when a directory user's password couldn't be checked at all (no agent online, directory unreachable).</summary>
public sealed class DirectorySignInResult : SignInResult
{
    public static readonly DirectorySignInResult Unavailable = new();
    private DirectorySignInResult() { }
    public override string ToString() => "DirectoryUnavailable";
}

/// <summary>
/// Sends a directory (LDAP) user's password to the company directory instead of comparing it with a local hash
/// (spec §8.1). The override sits beneath <see cref="SignInManager{TUser}.PasswordSignInAsync(string,string,bool,bool)"/>,
/// so everything around the password check - deactivation, lockout, two-factor, the cookie - is the stock Identity
/// behaviour and is identical for both kinds of user.
/// </summary>
public sealed class OrbitSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    DirectoryAuthService directory)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<SignInResult> CheckPasswordSignInAsync(ApplicationUser user, string password, bool lockoutOnFailure)
    {
        ArgumentNullException.ThrowIfNull(user);
        // A local user's password never goes near the directory, and a directory user's PasswordHash (there
        // shouldn't be one) is never consulted: the two paths don't fall back to each other.
        if (user.AuthSource != AuthSource.Ldap)
            return await base.CheckPasswordSignInAsync(user, password, lockoutOnFailure);

        // Deactivated or locked out: don't bother the directory.
        if (await PreSignInCheck(user) is { } blocked) return blocked;

        var outcome = await directory.AuthenticateAsync(user.Email ?? string.Empty, password, Context.RequestAborted);
        switch (outcome.Status)
        {
            case DirectoryAuthStatus.Success:
                // Same condition as the base class: with two-factor pending, the count resets after the second step.
                if (!await IsTwoFactorEnabledAsync(user) || await IsTwoFactorClientRememberedAsync(user))
                    await ResetLockout(user);
                return SignInResult.Success;

            case DirectoryAuthStatus.Unavailable:
                // An outage is not a wrong guess: counting it would lock people out for something they didn't do.
                return DirectorySignInResult.Unavailable;

            default:
                // Count failures in Orbit as well, so it can't be used to guess directory passwords at leisure.
                if (UserManager.SupportsUserLockout && lockoutOnFailure)
                {
                    var counted = await UserManager.AccessFailedAsync(user);
                    if (counted.Succeeded && await UserManager.IsLockedOutAsync(user))
                        return await LockedOut(user);
                }
                return SignInResult.Failed;
        }
    }
}

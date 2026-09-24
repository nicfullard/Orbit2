using Microsoft.AspNetCore.Authorization;
using Orbit.Application;

namespace Orbit.Auth;

/// <summary>A page or folder needs this permission at any scope (spec §6.5); the service applies the scope.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Answers <see cref="PermissionRequirement"/> from the current actor's grants - read from the database per request by
/// <see cref="IActorProvider"/>, so a role edit or a deactivation bites on the very next page load, not when the cookie refreshes.
/// </summary>
public sealed class PermissionAuthorizationHandler(IActorProvider actors) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        Actor actor;
        try
        {
            actor = await actors.GetAsync();
        }
        catch (ForbiddenException)
        {
            return; // deactivated or unknown: fail closed
        }
        if (actor.Has(requirement.Permission)) context.Succeed(requirement);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>Adds Orbit's display-name / department / actor-type claims to the Identity cookie.</summary>
public sealed class OrbitClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(OrbitClaims.DisplayName, user.DisplayName));
        identity.AddClaim(new Claim(OrbitClaims.ActorType, nameof(ActorType.User)));
        identity.AddClaim(new Claim(OrbitClaims.AuthSource, user.AuthSource.ToString()));
        if (user.DepartmentId is Guid dept)
            identity.AddClaim(new Claim(OrbitClaims.DepartmentId, dept.ToString()));
        return identity;
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>
/// Resolves the <see cref="Actor"/> for the current HTTP request. Both kinds of caller get their role's grants from the
/// database on every request: an API key through the role id in its claims, a cookie user by re-reading the user, their
/// role and its grants - so a role edit, a department change or a deactivation takes effect on the next request rather
/// than when the cookie refreshes. A principal whose role can't be resolved gets no grants at all.
/// </summary>
public sealed class HttpActorProvider(IHttpContextAccessor accessor, ApplicationDbContext db) : IActorProvider
{
    private Actor? _cached;

    public async Task<Actor> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var principal = accessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            throw new ForbiddenException("Sign in required.");

        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var userId))
            throw new ForbiddenException("The signed-in identity is not an Orbit user.");

        if (principal.FindFirstValue(OrbitClaims.ActorType) == nameof(ActorType.Api))
        {
            var role = Guid.TryParse(principal.FindFirstValue(OrbitClaims.RoleId), out var roleId)
                ? await RoleResolver.ForRoleAsync(db, roleId, ct) ?? ResolvedRole.None
                : ResolvedRole.None;
            Guid? dept = Guid.TryParse(principal.FindFirstValue(OrbitClaims.DepartmentId), out var d) ? d : null;
            var apiKeyId = Guid.TryParse(principal.FindFirstValue(OrbitClaims.ApiKeyId), out var k) ? k : Guid.Empty;
            var name = principal.FindFirstValue(OrbitClaims.DisplayName) ?? WellKnownIds.ClaudeAgentDisplayName;
            return _cached = new Actor(userId, name, role.Ref, role.Permissions, dept, ActorType.Api, apiKeyId);
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.DepartmentId, u.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new ForbiddenException("Your account no longer exists.");
        if (!user.IsActive)
            throw new ForbiddenException("Your account has been deactivated.");

        var roles = await RoleResolver.ForUsersAsync(db, [userId], ct);
        var userRole = roles.GetValueOrDefault(userId) ?? ResolvedRole.None;
        return _cached = new Actor(userId, user.DisplayName, userRole.Ref, userRole.Permissions, user.DepartmentId, ActorType.User, userId);
    }
}

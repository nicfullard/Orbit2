using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>
/// Resolves the <see cref="Actor"/> for the current HTTP request. API-key callers are built from their
/// claims (fresh on every request); cookie users are re-read from the database so a role/department
/// change or deactivation takes effect immediately rather than when the cookie refreshes.
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
            var role = MaxRole(principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
            Guid? dept = Guid.TryParse(principal.FindFirstValue(OrbitClaims.DepartmentId), out var d) ? d : null;
            var apiKeyId = Guid.TryParse(principal.FindFirstValue(OrbitClaims.ApiKeyId), out var k) ? k : Guid.Empty;
            var name = principal.FindFirstValue(OrbitClaims.DisplayName) ?? WellKnownIds.ClaudeAgentDisplayName;
            return _cached = new Actor(userId, name, role, dept, ActorType.Api, apiKeyId);
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.DepartmentId, u.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new ForbiddenException("Your account no longer exists.");
        if (!user.IsActive)
            throw new ForbiddenException("Your account has been deactivated.");

        var roles = await UserDirectoryService.GetRolesAsync(db, [userId], ct);
        var userRole = roles.TryGetValue(userId, out var r) ? r : OrbitRole.Member;
        return _cached = new Actor(userId, user.DisplayName, userRole, user.DepartmentId, ActorType.User, userId);
    }

    private static OrbitRole MaxRole(IEnumerable<string> names)
    {
        var best = OrbitRole.Member;
        foreach (var n in names)
            if (Enum.TryParse<OrbitRole>(n, out var role) && role > best) best = role;
        return best;
    }
}

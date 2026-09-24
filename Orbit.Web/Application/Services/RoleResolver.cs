using Microsoft.EntityFrameworkCore;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>A role with its grants resolved (spec §6.5): the built-in role is every permission at All, any other its stored rows.</summary>
public sealed record ResolvedRole(Guid Id, string Name, bool IsBuiltIn, IReadOnlyDictionary<string, PermissionScope> Permissions)
{
    /// <summary>A user or key with no role row: no grants.</summary>
    public static readonly ResolvedRole None = new(Guid.Empty, RoleRef.None.Name, false, new Dictionary<string, PermissionScope>());

    public PermissionScope ScopeOf(string permission) => Permissions.GetValueOrDefault(permission);

    public bool RequiresDepartment => RoleRules.RequiresDepartment(Permissions);

    public RoleRef Ref => new(Id, Name, IsBuiltIn, IsBuiltIn || Permissions.Values.Any(s => s == PermissionScope.All));
}

/// <summary>
/// Turns role rows into grants, per request and without caching (the database is the source of truth, so a role edit
/// bites on the next request). One role per user; if a user somehow has several, the built-in one wins, else the first by name.
/// </summary>
public static class RoleResolver
{
    public static ResolvedRole Resolve(ApplicationRole role) => new(
        role.Id,
        role.Name ?? string.Empty,
        role.IsBuiltIn,
        role.IsBuiltIn
            ? PermissionCatalog.AllAtScopeAll
            : role.Permissions.Where(p => p.Scope != PermissionScope.None)
                .ToDictionary(p => p.Permission, p => p.Scope, StringComparer.Ordinal));

    public static async Task<ResolvedRole?> ForRoleAsync(ApplicationDbContext db, Guid roleId, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return role is null ? null : Resolve(role);
    }

    /// <summary>The role of each of the given users; users with no role row are absent from the result.</summary>
    public static async Task<Dictionary<Guid, ResolvedRole>> ForUsersAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ResolvedRole>();
        if (userIds.Count == 0) return result;
        var memberships = await db.UserRoles.AsNoTracking().Where(ur => userIds.Contains(ur.UserId)).ToListAsync(ct);
        if (memberships.Count == 0) return result;
        var roleIds = memberships.Select(m => m.RoleId).Distinct().ToList();
        var roles = (await db.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync(ct))
            .ToDictionary(r => r.Id, Resolve);
        foreach (var group in memberships.GroupBy(m => m.UserId))
        {
            var chosen = group.Select(m => roles.GetValueOrDefault(m.RoleId)).Where(r => r is not null)
                .OrderByDescending(r => r!.IsBuiltIn).ThenBy(r => r!.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (chosen is not null) result[group.Key] = chosen;
        }
        return result;
    }

    /// <summary>Whether a user's role grants a permission at least at the given scope - e.g. tasks.view at All, which makes a user assignable in any department.</summary>
    public static async Task<bool> HasScopeAsync(ApplicationDbContext db, Guid userId, string permission, PermissionScope scope, CancellationToken ct)
    {
        var roles = await ForUsersAsync(db, [userId], ct);
        return roles.TryGetValue(userId, out var role) && role.ScopeOf(permission) >= scope;
    }

    /// <summary>Ids of the users whose role grants <paramref name="permission"/> at All: the built-in role's members plus any role with that row.</summary>
    public static async Task<List<Guid>> UserIdsWithScopeAllAsync(ApplicationDbContext db, string permission, CancellationToken ct)
    {
        var roleIds = await db.Roles.AsNoTracking()
            .Where(r => r.IsBuiltIn || r.Permissions.Any(p => p.Permission == permission && p.Scope == PermissionScope.All))
            .Select(r => r.Id).ToListAsync(ct);
        if (roleIds.Count == 0) return [];
        return await db.UserRoles.AsNoTracking().Where(ur => roleIds.Contains(ur.RoleId)).Select(ur => ur.UserId).Distinct().ToListAsync(ct);
    }
}

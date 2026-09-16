using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>User lookups available to every role (pickers, the list_users tool). Scoped to the caller's department unless System Admin.</summary>
public sealed class UserDirectoryService(ApplicationDbContext db, IActorProvider actors)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(string? query, Guid? departmentId, bool includeInactive = false, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = db.Users.AsNoTracking().Include(u => u.Department).Where(u => !u.IsSystemAccount);
        if (!includeInactive) q = q.Where(u => u.IsActive);
        if (!actor.IsSystemAdmin) q = q.Where(u => u.DepartmentId == actor.DepartmentId);
        else if (departmentId is Guid d) q = q.Where(u => u.DepartmentId == d);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            q = q.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Email!, pattern));
        }
        var users = await q.OrderBy(u => u.DisplayName).ToListAsync(ct);
        var roles = await GetRolesAsync(db, users.Select(u => u.Id).ToList(), ct);
        return users.Select(u => ToSummary(u, roles)).ToList();
    }

    /// <summary>Active users a task in the given department may be assigned to: its members plus System Admins.</summary>
    public async Task<IReadOnlyList<UserSummary>> GetAssignableAsync(Guid departmentId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var systemAdminIds = await db.UserRoles
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name == Roles.SystemAdmin).Select(x => x.UserId).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => u.IsActive && !u.IsSystemAccount && (u.DepartmentId == departmentId || systemAdminIds.Contains(u.Id)))
            .OrderBy(u => u.DisplayName).ToListAsync(ct);
        var roles = await GetRolesAsync(db, users.Select(u => u.Id).ToList(), ct);
        return users.Select(u => ToSummary(u, roles)).ToList();
    }

    /// <summary>
    /// Everyone the caller could assign a task to, across every department they can see - the candidate list for
    /// inline assignee controls on task lists that may mix departments (sprints, cross-department projects).
    /// System Admin: all active users; otherwise the caller's own department plus System Admins.
    /// The list control filters per row to the task's department plus System Admins.
    /// </summary>
    public async Task<IReadOnlyList<UserSummary>> GetQuickEditCandidatesAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.IsSystemAdmin) return await ListAsync(null, null, false, ct);
        return actor.DepartmentId is Guid d ? await GetAssignableAsync(d, ct) : [];
    }

    public async Task<UserSummary?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var user = await db.Users.AsNoTracking().Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return null;
        var roles = await GetRolesAsync(db, [id], ct);
        return ToSummary(user, roles);
    }

    public static async Task<Dictionary<Guid, OrbitRole>> GetRolesAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await db.UserRoles.Where(ur => userIds.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(ct);
        var result = new Dictionary<Guid, OrbitRole>();
        foreach (var row in rows)
        {
            if (!Enum.TryParse<OrbitRole>(row.Name, out var role)) continue;
            if (!result.TryGetValue(row.UserId, out var existing) || role > existing) result[row.UserId] = role;
        }
        return result;
    }

    public static UserSummary ToSummary(ApplicationUser u, IReadOnlyDictionary<Guid, OrbitRole> roles) =>
        new(u.Id, u.DisplayName, u.Email ?? string.Empty,
            roles.TryGetValue(u.Id, out var role) ? role : OrbitRole.Member,
            u.DepartmentId, u.Department?.Name, u.IsActive, u.IsSystemAccount, u.CreatedAt);
}

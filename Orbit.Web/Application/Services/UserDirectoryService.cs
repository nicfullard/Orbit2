using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// User lookups available to every role (pickers, the list_users tool). Scoped to the caller's department unless
/// their role sees tasks everywhere (tasks.view at All) or edits assets (assets.edit at any scope): assets are issued
/// across departments, so asset managers need to find anyone (spec §6.19). Only names, emails and departments show.
/// </summary>
public sealed class UserDirectoryService(ApplicationDbContext db, IActorProvider actors)
{
    /// <summary>Whether the caller's user lookups cover every department.</summary>
    public static bool SeesEveryDepartment(Actor actor) => actor.CanAnywhere(Permission.TasksView) || actor.Has(Permission.AssetsEdit);

    public async Task<IReadOnlyList<UserSummary>> ListAsync(string? query, Guid? departmentId, bool includeInactive = false, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = db.Users.AsNoTracking().Include(u => u.Department).Where(u => !u.IsSystemAccount);
        if (!includeInactive) q = q.Where(u => u.IsActive);
        if (!SeesEveryDepartment(actor)) q = q.Where(u => u.DepartmentId == actor.DepartmentId);
        else if (departmentId is Guid d) q = q.Where(u => u.DepartmentId == d);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            q = q.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Email!, pattern));
        }
        var users = await q.OrderBy(u => u.DisplayName).ToListAsync(ct);
        return await ToSummariesAsync(db, users, ct);
    }

    /// <summary>Active users a task in the given department may be assigned to: its members plus anyone whose role sees tasks everywhere.</summary>
    public async Task<IReadOnlyList<UserSummary>> GetAssignableAsync(Guid departmentId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var everywhere = await RoleResolver.UserIdsWithScopeAllAsync(db, Permission.TasksView, ct);
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => u.IsActive && !u.IsSystemAccount && (u.DepartmentId == departmentId || everywhere.Contains(u.Id)))
            .OrderBy(u => u.DisplayName).ToListAsync(ct);
        return await ToSummariesAsync(db, users, ct);
    }

    /// <summary>
    /// Everyone the caller could assign a task to, across every department they can see - the candidate list for
    /// inline assignee controls on task lists that may mix departments (sprints, cross-department projects).
    /// tasks.view at All: all active users; otherwise the caller's own department plus anyone assignable anywhere.
    /// The list control filters per row to the task's department plus those users.
    /// </summary>
    public async Task<IReadOnlyList<UserSummary>> GetQuickEditCandidatesAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.CanAnywhere(Permission.TasksView)) return await ListAsync(null, null, false, ct);
        return actor.DepartmentId is Guid d ? await GetAssignableAsync(d, ct) : [];
    }

    /// <summary>
    /// People who can be given an asset (spec §6.19) matching what was typed into the holder picker: active people - never the
    /// synthetic Claude user - whose name, email or department contains <paramref name="query"/>, names starting with it first.
    /// Anyone in any department can hold an asset, so the search spans the organisation; it answers only callers who may register
    /// or edit assets, and returns at most <paramref name="limit"/> people rather than the whole directory.
    /// </summary>
    public async Task<IReadOnlyList<UserSummary>> SearchAssetHolderCandidatesAsync(string? query, int limit = 20, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.Has(Permission.AssetsCreate) && !actor.Has(Permission.AssetsEdit)) return [];
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text)) return [];
        var anywhere = $"%{text}%";
        var prefix = $"{text}%";
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => u.IsActive && !u.IsSystemAccount
                && (EF.Functions.ILike(u.DisplayName, anywhere) || EF.Functions.ILike(u.Email!, anywhere)
                    || (u.Department != null && EF.Functions.ILike(u.Department.Name, anywhere))))
            .OrderBy(u => EF.Functions.ILike(u.DisplayName, prefix) ? 0 : 1).ThenBy(u => u.DisplayName)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(ct);
        return await ToSummariesAsync(db, users, ct);
    }

    /// <summary>The given people, by name - e.g. an asset's holders shown as chips in the picker.</summary>
    public async Task<IReadOnlyList<UserSummary>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        if (ids.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => ids.Contains(u.Id)).OrderBy(u => u.DisplayName).ToListAsync(ct);
        return await ToSummariesAsync(db, users, ct);
    }

    public async Task<UserSummary?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var user = await db.Users.AsNoTracking().Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return null;
        var roles = await RoleResolver.ForUsersAsync(db, [id], ct);
        return ToSummary(user, roles);
    }

    /// <summary>Whether a user's role grants a permission at least at the given scope (e.g. tasks.view at All makes them assignable anywhere).</summary>
    public Task<bool> HasScopeAsync(Guid userId, string permission, PermissionScope scope, CancellationToken ct = default) =>
        RoleResolver.HasScopeAsync(db, userId, permission, scope, ct);

    public static async Task<IReadOnlyList<UserSummary>> ToSummariesAsync(ApplicationDbContext db, IReadOnlyList<ApplicationUser> users, CancellationToken ct)
    {
        var roles = await RoleResolver.ForUsersAsync(db, users.Select(u => u.Id).ToList(), ct);
        return users.Select(u => ToSummary(u, roles)).ToList();
    }

    public static UserSummary ToSummary(ApplicationUser u, IReadOnlyDictionary<Guid, ResolvedRole> roles)
    {
        var role = roles.GetValueOrDefault(u.Id) ?? ResolvedRole.None;
        return new UserSummary(u.Id, u.DisplayName, u.Email ?? string.Empty, role.Ref, role.ScopeOf(Permission.TasksView) == PermissionScope.All,
            u.DepartmentId, u.Department?.Name, u.IsActive, u.IsSystemAccount, u.CreatedAt, u.AuthSource, u.LockoutEnd);
    }
}

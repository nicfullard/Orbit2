using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Admin &gt; Roles (spec §6.5): roles as data. Create, edit and delete roles and their grants under the
/// <see cref="RoleRules"/>; the built-in System Administrator role is read-only here. Every change is audited with
/// each grant's old and new scope.
/// </summary>
public sealed class RoleService(ApplicationDbContext db, RoleManager<ApplicationRole> roleManager, IActorProvider actors, AuditService audit)
{
    public async Task<IReadOnlyList<RoleListItem>> ListAsync(CancellationToken ct = default)
    {
        await RequireManagerAsync(ct);
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions)
            .OrderByDescending(r => r.IsBuiltIn).ThenBy(r => r.Name).ToListAsync(ct);
        var users = await CountUsersAsync(ct);
        var keys = await CountKeysAsync(ct);
        return roles.Select(r => new RoleListItem(r, RoleResolver.Resolve(r), users.GetValueOrDefault(r.Id), keys.GetValueOrDefault(r.Id))).ToList();
    }

    /// <summary>Every role, for the role dropdowns on the Users and API keys pages (their own permissions open those pages).</summary>
    public async Task<IReadOnlyList<RolePickerItem>> ListForPickerAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageRoles(actor) || AccessPolicy.CanManageUsers(actor) || AccessPolicy.CanManageApiKeys(actor),
            "You don't have permission to see the roles.");
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions)
            .OrderByDescending(r => r.IsBuiltIn).ThenBy(r => r.Name).ToListAsync(ct);
        return roles.Select(r =>
        {
            var resolved = RoleResolver.Resolve(r);
            return new RolePickerItem(r.Id, r.Name ?? string.Empty, r.Description, r.IsBuiltIn, resolved.RequiresDepartment);
        }).ToList();
    }

    public async Task<RoleListItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        await RequireManagerAsync(ct);
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Role not found.");
        return new RoleListItem(role, RoleResolver.Resolve(role), await CountUsersAsync(id, ct), await CountKeysAsync(id, ct));
    }

    public async Task<ApplicationRole> CreateAsync(RoleInput input, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var name = RoleRules.ValidateName(input.Name);
        var description = RoleRules.ValidateDescription(input.Description);
        var grants = RoleRules.Validate(input.Grants);
        if (await roleManager.FindByNameAsync(name) is not null)
            throw new ValidationException($"A role named \"{name}\" already exists.");

        var role = new ApplicationRole(name) { Description = description, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Throw(await roleManager.CreateAsync(role));
        foreach (var (permission, scope) in grants)
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = permission, Scope = scope });
        audit.Add(actor, AuditEntity.Role, role.Id, AuditAction.Created, null, role.Name,
            new { name, description, grants = grants.ToDictionary(g => g.Key, g => g.Value.ToString()) });
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<ApplicationRole> UpdateAsync(Guid id, RoleInput input, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Role not found.");
        RoleRules.RequireEditable(role);
        var name = RoleRules.ValidateName(input.Name);
        var description = RoleRules.ValidateDescription(input.Description);
        var grants = RoleRules.Validate(input.Grants);
        var duplicate = await roleManager.FindByNameAsync(name);
        if (duplicate is not null && duplicate.Id != id)
            throw new ValidationException($"A role named \"{name}\" already exists.");

        var current = RoleResolver.Resolve(role).Permissions;
        if (!RoleRules.RequiresDepartment(current) && RoleRules.RequiresDepartment(grants))
            await RequireEveryoneHasDepartmentAsync(role, ct);

        var changes = new ChangeSet()
            .TrackText("name", role.Name, name)
            .TrackText("description", role.Description, description);
        foreach (var key in PermissionCatalog.All.Select(p => p.Key).Union(current.Keys).Union(grants.Keys))
            changes.Track(key, current.GetValueOrDefault(key), grants.GetValueOrDefault(key));
        if (!changes.HasChanges) return role;

        if (role.Name != name)
        {
            // Through the RoleManager so NormalizedName and the concurrency stamp are kept right.
            Throw(await roleManager.SetRoleNameAsync(role, name));
        }
        role.Description = description;
        role.UpdatedAt = DateTime.UtcNow;
        Throw(await roleManager.UpdateAsync(role));

        db.RolePermissions.RemoveRange(role.Permissions);
        foreach (var (permission, scope) in grants)
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = permission, Scope = scope });

        audit.Add(actor, AuditEntity.Role, role.Id, AuditAction.Updated, null, name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return role;
    }

    /// <summary>Only an unused role can go: the users and keys in it (revoked keys too) would be left without one.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Role not found.");
        RoleRules.RequireEditable(role);
        var users = await CountUsersAsync(id, ct);
        var keys = await CountKeysAsync(id, ct);
        if (users > 0 || keys > 0)
            throw new ValidationException($"\"{role.Name}\" is still in use by {Count(users, "user")} and {Count(keys, "API key")}. Move them to another role first.");

        Throw(await roleManager.DeleteAsync(role));
        audit.Add(actor, AuditEntity.Role, role.Id, AuditAction.Deleted, null, role.Name);
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireEveryoneHasDepartmentAsync(ApplicationRole role, CancellationToken ct)
    {
        var userIds = db.UserRoles.Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId);
        var users = await db.Users.CountAsync(u => userIds.Contains(u.Id) && u.IsActive && u.DepartmentId == null, ct);
        var keys = await db.ApiKeys.CountAsync(k => k.RoleId == role.Id && k.RevokedAt == null && k.DepartmentId == null, ct);
        if (users > 0 || keys > 0)
            throw new ValidationException(
                $"A grant at Department scope means everyone in the role needs a department, but {Count(users, "user")} and {Count(keys, "API key")} in \"{role.Name}\" have none. Give them a department first.");
    }

    private async Task<Actor> RequireManagerAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageRoles(actor), "You don't have permission to manage roles.");
        return actor;
    }

    private async Task<Dictionary<Guid, int>> CountUsersAsync(CancellationToken ct) =>
        await db.UserRoles.GroupBy(ur => ur.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private Task<int> CountUsersAsync(Guid roleId, CancellationToken ct) => db.UserRoles.CountAsync(ur => ur.RoleId == roleId, ct);

    private async Task<Dictionary<Guid, int>> CountKeysAsync(CancellationToken ct) =>
        await db.ApiKeys.GroupBy(k => k.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private Task<int> CountKeysAsync(Guid roleId, CancellationToken ct) => db.ApiKeys.CountAsync(k => k.RoleId == roleId, ct);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static void Throw(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>System Admin user management: create, promote/demote, move between departments, deactivate, reset password.</summary>
public sealed class UserAdminService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IActorProvider actors,
    AuditService audit,
    LdapSettingsService ldapSettings)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => !u.IsSystemAccount)
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName).ToListAsync(ct);
        var roles = await UserDirectoryService.GetRolesAsync(db, users.Select(u => u.Id).ToList(), ct);
        return users.Select(u => UserDirectoryService.ToSummary(u, roles)).ToList();
    }

    public async Task<UserSummary> GetAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var user = await db.Users.AsNoTracking().Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id && !u.IsSystemAccount, ct)
            ?? throw new NotFoundException("User not found.");
        var roles = await UserDirectoryService.GetRolesAsync(db, [id], ct);
        return UserDirectoryService.ToSummary(user, roles);
    }

    public async Task<UserSummary> CreateAsync(UserInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var email = RequireEmail(input.Email);
        var displayName = RequireDisplayName(input.DisplayName);
        var departmentId = await ValidateDepartmentForRoleAsync(input.Role, input.DepartmentId, ct);
        var isDirectoryUser = input.AuthSource == AuthSource.Ldap;
        if (isDirectoryUser)
            await RequireDirectorySignInEnabledAsync(ct);
        else if (string.IsNullOrWhiteSpace(input.Password))
            throw new ValidationException("A temporary password is required; the user changes it after first sign-in.");
        if (await userManager.FindByEmailAsync(email) is not null)
            throw new ValidationException($"A user with the email {email} already exists.");

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            DepartmentId = departmentId,
            AuthSource = input.AuthSource,
            IsActive = true,
            LockoutEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        // A directory user gets no password hash at all: the directory is the only thing that can vouch for them.
        Throw(isDirectoryUser ? await userManager.CreateAsync(user) : await userManager.CreateAsync(user, input.Password!));
        Throw(await userManager.AddToRoleAsync(user, input.Role.ToString()));

        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.Created, departmentId, displayName,
            new { email, role = input.Role, departmentId, authSource = input.AuthSource });
        await db.SaveChangesAsync(ct);
        return await GetAsync(user.Id, ct);
    }

    public async Task<UserSummary> UpdateAsync(Guid id, UserInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount) throw new ValidationException("The system account can't be edited.");

        var displayName = RequireDisplayName(input.DisplayName);
        var departmentId = await ValidateDepartmentForRoleAsync(input.Role, input.DepartmentId, ct);
        var currentRoles = await userManager.GetRolesAsync(user);
        var currentRole = currentRoles.Select(r => Enum.TryParse<OrbitRole>(r, out var x) ? x : OrbitRole.Member)
            .DefaultIfEmpty(OrbitRole.Member).Max();

        var isLocalSystemAdmin = currentRole == OrbitRole.SystemAdmin && user.AuthSource == AuthSource.Local && user.IsActive;
        var staysLocalSystemAdmin = input.Role == OrbitRole.SystemAdmin && input.AuthSource == AuthSource.Local;
        if (isLocalSystemAdmin && !staysLocalSystemAdmin)
            await RequireAnotherLocalSystemAdminAsync(user.Id, ct);
        if (input.AuthSource == AuthSource.Ldap && user.AuthSource != AuthSource.Ldap)
            await RequireDirectorySignInEnabledAsync(ct);

        var changes = new ChangeSet()
            .TrackText("displayName", user.DisplayName, displayName)
            .Track("departmentId", user.DepartmentId, departmentId)
            .Track("role", currentRole, input.Role)
            .Track("authSource", user.AuthSource, input.AuthSource);
        if (!changes.HasChanges) return await GetAsync(id, ct);

        user.DisplayName = displayName;
        user.DepartmentId = departmentId;
        if (user.AuthSource != input.AuthSource)
        {
            user.AuthSource = input.AuthSource;
            // Whichever way the switch goes, any stored hash goes with it: a directory user must not keep a usable
            // local password, and a user coming back to Local must not inherit one set while they were a directory user.
            user.PasswordHash = null;
        }
        Throw(await userManager.UpdateAsync(user));
        if (currentRole != input.Role)
        {
            if (currentRoles.Count > 0) Throw(await userManager.RemoveFromRolesAsync(user, currentRoles));
            Throw(await userManager.AddToRoleAsync(user, input.Role.ToString()));
        }
        // Refresh the user's cookie claims on their next request.
        Throw(await userManager.UpdateSecurityStampAsync(user));

        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.Updated, departmentId, displayName, changes.Changes);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Soft delete: the account can't sign in, disappears from pickers, but its history stays attributed.</summary>
    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        if (actor.UserId == id) throw new ValidationException("You can't deactivate your own account.");
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount) throw new ValidationException("The system account can't be deactivated.");
        if (!user.IsActive) return;
        if (user.AuthSource == AuthSource.Local && await userManager.IsInRoleAsync(user, Roles.SystemAdmin))
            await RequireAnotherLocalSystemAdminAsync(user.Id, ct);

        user.IsActive = false;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        Throw(await userManager.UpdateAsync(user));
        Throw(await userManager.UpdateSecurityStampAsync(user));
        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.Deactivated, user.DepartmentId, user.DisplayName);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReactivateAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount || user.IsActive) return;
        user.IsActive = true;
        user.LockoutEnd = null;
        Throw(await userManager.UpdateAsync(user));
        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.Reactivated, user.DepartmentId, user.DisplayName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ends a wrong-password lockout early. Lockouts are long on purpose (they have to outlast Active Directory's own
    /// counter, see <see cref="LockoutSettings"/>), so someone who simply forgot their password shouldn't have to wait one out.
    /// For a directory user this clears Orbit's lock only; if AD has locked the account too, that is lifted in AD.
    /// </summary>
    public async Task<UserSummary> UnlockAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount || !user.IsActive) throw new ValidationException("Only an active user can be unlocked. Reactivate a deactivated user instead.");
        if (user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            Throw(await userManager.SetLockoutEndDateAsync(user, null));
            Throw(await userManager.ResetAccessFailedCountAsync(user));
            audit.Add(actor, AuditEntity.User, user.Id, AuditAction.Unlocked, user.DepartmentId, user.DisplayName);
            await db.SaveChangesAsync(ct);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>Identity's standard reset token; the page turns it into a one-time link the admin hands to the user.</summary>
    public async Task<(ApplicationUser User, string Token)> GeneratePasswordResetTokenAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount || !user.IsActive) throw new ValidationException("Passwords can only be reset for active users.");
        RequireLocalPassword(user);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.PasswordReset, user.DepartmentId, user.DisplayName, new { method = "link" });
        await db.SaveChangesAsync(ct);
        return (user, token);
    }

    public async Task SetTemporaryPasswordAsync(Guid id, string password, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User not found.");
        if (user.IsSystemAccount || !user.IsActive) throw new ValidationException("Passwords can only be reset for active users.");
        RequireLocalPassword(user);
        if (string.IsNullOrWhiteSpace(password)) throw new ValidationException("A password is required.");
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        Throw(await userManager.ResetPasswordAsync(user, token, password));
        Throw(await userManager.UpdateSecurityStampAsync(user));
        audit.Add(actor, AuditEntity.User, user.Id, AuditAction.PasswordReset, user.DepartmentId, user.DisplayName, new { method = "temporaryPassword" });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageUsers(actor), "Only a System Admin can manage users.");
        return actor;
    }

    /// <summary>
    /// The break-glass rule: someone must always be able to administer Orbit without the directory. If every System
    /// Admin signed in through LDAP, an agent or directory outage would lock out the only people able to fix it.
    /// </summary>
    private async Task RequireAnotherLocalSystemAdminAsync(Guid excludingUserId, CancellationToken ct)
    {
        var others = await db.UserRoles
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name == Roles.SystemAdmin && x.UserId != excludingUserId)
            .Join(db.Users, x => x.UserId, u => u.Id, (x, u) => u)
            .CountAsync(u => u.IsActive && u.AuthSource == AuthSource.Local, ct);
        if (others == 0)
            throw new ValidationException("At least one active System Admin with a local password must remain, so Orbit can still be administered if the directory or its agent is unavailable.");
    }

    private async Task RequireDirectorySignInEnabledAsync(CancellationToken ct)
    {
        if (!await ldapSettings.IsEnabledAsync(ct))
            throw new ValidationException("Directory (LDAP) sign-in isn't enabled yet. Set it up under Admin > Directory first.");
    }

    private static void RequireLocalPassword(ApplicationUser user)
    {
        if (user.AuthSource == AuthSource.Ldap)
            throw new ValidationException("This user signs in with the company directory; their password is managed there, not in Orbit.");
    }

    private async Task<Guid?> ValidateDepartmentForRoleAsync(OrbitRole role, Guid? departmentId, CancellationToken ct)
    {
        if (role != OrbitRole.SystemAdmin && departmentId is null)
            throw new ValidationException("Members and Department Admins must belong to a department.");
        if (departmentId is Guid id)
        {
            var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
                ?? throw new NotFoundException("Department not found.");
            if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        }
        return departmentId;
    }

    private static string RequireEmail(string? email)
    {
        var e = email?.Trim();
        if (string.IsNullOrEmpty(e) || !e.Contains('@')) throw new ValidationException("A valid email address is required.");
        return e;
    }

    private static string RequireDisplayName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("Display name is required.");
        if (n.Length > 200) throw new ValidationException("Display name must be 200 characters or fewer.");
        return n;
    }

    private static void Throw(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

using Microsoft.AspNetCore.Identity;

namespace Orbit.Data.Entities;

/// <summary>
/// A role (spec §6.5): a named set of permission grants, each at a scope, stored in <see cref="RolePermission"/> rows.
/// Extends Identity's role so membership (<c>AspNetUserRoles</c>) and the <c>UserManager</c> role APIs keep working.
/// The one built-in role (<see cref="IsBuiltIn"/>) stores no grants: it resolves to every permission at All.
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }

    public ApplicationRole(string name) : base(name) { }

    public string? Description { get; set; }

    /// <summary>The System Administrator role: immutable, undeletable, every permission at All - including ones added later.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}

/// <summary>One grant on a role: a permission key from the catalogue at a scope (spec §6.5).</summary>
public class RolePermission
{
    public Guid RoleId { get; set; }
    public ApplicationRole Role { get; set; } = null!;
    /// <summary>A <see cref="Orbit.Application.Permission"/> key, e.g. <c>tasks.edit</c>.</summary>
    public string Permission { get; set; } = string.Empty;
    public Orbit.Application.PermissionScope Scope { get; set; }
}

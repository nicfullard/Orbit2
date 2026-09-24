using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>What the role editor posts (spec §6.5): a name, a description and a scope per permission (None = not granted).</summary>
public sealed class RoleInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public IReadOnlyDictionary<string, PermissionScope> Grants { get; set; } = new Dictionary<string, PermissionScope>();
}

/// <summary>A role as Admin &gt; Roles lists it.</summary>
public sealed record RoleListItem(ApplicationRole Role, ResolvedRole Resolved, int UserCount, int KeyCount)
{
    public RoleRef Ref => Resolved.Ref;
    /// <summary>Grants the role holds; the built-in role counts the whole catalogue.</summary>
    public int GrantCount => Resolved.Permissions.Count;
    public bool RequiresDepartment => Resolved.RequiresDepartment;
}

/// <summary>An option for the role dropdowns on the Users and API keys pages.</summary>
public sealed record RolePickerItem(Guid Id, string Name, string? Description, bool IsBuiltIn, bool RequiresDepartment);

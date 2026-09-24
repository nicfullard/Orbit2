using System.ComponentModel.DataAnnotations;
using Orbit.Application;
using Orbit.Application.Models;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Roles;

/// <summary>One row of the role editor: a permission key and the scope chosen for it (blank or None = not granted).</summary>
public sealed class GrantForm
{
    public string? Permission { get; set; }
    public string? Scope { get; set; }
}

public sealed class RoleForm
{
    [Required, StringLength(RoleRules.MaxNameLength)] public string Name { get; set; } = string.Empty;
    [StringLength(RoleRules.MaxDescriptionLength)] public string? Description { get; set; }
    /// <summary>One row per grantable catalogue entry, in catalogue order; reserved permissions are never posted.</summary>
    public List<GrantForm> Grants { get; set; } = [];

    public RoleInput ToInput()
    {
        var grants = new Dictionary<string, PermissionScope>(StringComparer.Ordinal);
        foreach (var g in Grants)
        {
            if (string.IsNullOrWhiteSpace(g.Permission)) continue;
            PermissionScope scope;
            if (string.IsNullOrWhiteSpace(g.Scope)) scope = PermissionScope.None;
            else if (Enum.TryParse(g.Scope.Trim(), ignoreCase: true, out scope) && Enum.IsDefined(scope)) { }
            else throw new ValidationException($"\"{g.Scope}\" is not a scope.");
            grants[g.Permission.Trim()] = scope;
        }
        return new RoleInput { Name = Name, Description = Description, Grants = grants };
    }

    public static RoleForm Empty() => new() { Grants = Rows(new Dictionary<string, PermissionScope>()) };

    /// <summary>
    /// Re-renders a posted form as the editor expects it: one row per grantable permission, in catalogue order, keeping the
    /// scopes that were posted. A partial or forged post (a missing row, a reserved or unknown key) is shown, not crashed on;
    /// the service has already refused it.
    /// </summary>
    public void Normalize()
    {
        var posted = Grants.Where(g => !string.IsNullOrWhiteSpace(g.Permission))
            .GroupBy(g => g.Permission!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Scope, StringComparer.Ordinal);
        Grants = PermissionCatalog.All.Where(p => !p.SystemAdministratorOnly)
            .Select(p => new GrantForm { Permission = p.Key, Scope = posted.GetValueOrDefault(p.Key) ?? nameof(PermissionScope.None) })
            .ToList();
    }

    public static RoleForm From(RoleListItem role) => new()
    {
        Name = role.Role.Name ?? string.Empty,
        Description = role.Role.Description,
        Grants = Rows(role.Resolved.Permissions)
    };

    private static List<GrantForm> Rows(IReadOnlyDictionary<string, PermissionScope> grants) =>
        PermissionCatalog.All.Where(p => !p.SystemAdministratorOnly)
            .Select(p => new GrantForm { Permission = p.Key, Scope = grants.GetValueOrDefault(p.Key).ToString() })
            .ToList();
}

/// <summary>What the grants partial renders: the editable form, or the built-in role's fixed grants read-only.</summary>
public sealed record RoleFormView(RoleForm Form, bool ReadOnly, IReadOnlyDictionary<string, PermissionScope>? Fixed = null);

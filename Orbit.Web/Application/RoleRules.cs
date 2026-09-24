using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The safety rules for roles as data (spec §6.5), pure so they can be unit-tested: the built-in role is immutable, a grant
/// must name a catalogue permission that isn't reserved and use a scope it allows, and a role with any grant at Department
/// scope needs its users and keys to have a department.
/// </summary>
public static class RoleRules
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// Validates a set of grants as a role editor would post them, whatever the form sent. Returns the grants with
    /// <see cref="PermissionScope.None"/> entries dropped; throws <see cref="ValidationException"/> on the first problem.
    /// </summary>
    public static IReadOnlyDictionary<string, PermissionScope> Validate(IEnumerable<KeyValuePair<string, PermissionScope>> grants)
    {
        var result = new Dictionary<string, PermissionScope>(StringComparer.Ordinal);
        foreach (var (key, scope) in grants)
        {
            if (scope == PermissionScope.None) continue;
            var definition = PermissionCatalog.Find(key)
                ?? throw new ValidationException($"\"{key}\" is not a permission Orbit knows.");
            if (definition.SystemAdministratorOnly)
                throw new ValidationException($"\"{definition.Label}\" is reserved to the built-in System Administrator role and can't be granted to another role.");
            if (!Enum.IsDefined(scope) || !definition.Allows(scope))
                throw new ValidationException($"\"{definition.Label}\" can't be granted at the {scope.Label()} scope. Allowed: {string.Join(", ", definition.AllowedScopes.Select(s => s.Label()))}.");
            result[key] = scope;
        }
        return result;
    }

    /// <summary>A role with any grant at Department scope only makes sense for someone in a department.</summary>
    public static bool RequiresDepartment(IReadOnlyDictionary<string, PermissionScope> grants) =>
        grants.Values.Any(s => s == PermissionScope.Department);

    /// <summary>The built-in role's name, description and grants can't change, and it can't be deleted.</summary>
    public static void RequireEditable(ApplicationRole role)
    {
        if (role.IsBuiltIn)
            throw new ValidationException($"The built-in {role.Name} role can't be edited or deleted. It always holds every permission.");
    }

    public static string ValidateName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("A role name is required.");
        if (n.Length > MaxNameLength) throw new ValidationException($"The role name must be {MaxNameLength} characters or fewer.");
        return n;
    }

    public static string? ValidateDescription(string? description)
    {
        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (d is { Length: > MaxDescriptionLength }) throw new ValidationException($"The description must be {MaxDescriptionLength} characters or fewer.");
        return d;
    }
}

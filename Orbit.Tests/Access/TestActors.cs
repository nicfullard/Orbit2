using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Actors built from grants, the way the resolver builds them from role rows (spec §6.5).</summary>
internal static class TestActors
{
    public static Actor With(IReadOnlyDictionary<string, PermissionScope> grants, Guid? department, string name = "Custom", Guid? userId = null)
    {
        var role = new RoleRef(Guid.NewGuid(), name, false, grants.Values.Any(s => s == PermissionScope.All));
        return new Actor(userId ?? Guid.NewGuid(), name, role, grants, department, ActorType.User, Guid.NewGuid());
    }

    public static Actor Grants(Guid? department, params (string Permission, PermissionScope Scope)[] grants) =>
        With(grants.ToDictionary(g => g.Permission, g => g.Scope, StringComparer.Ordinal), department);

    /// <summary>The shipped Member role's default grants.</summary>
    public static Actor Member(Guid? department) => With(DefaultRoles.MemberGrants, department, DefaultRoles.Member);

    /// <summary>The shipped Department Admin role's default grants.</summary>
    public static Actor DepartmentAdmin(Guid? department) => With(DefaultRoles.DepartmentAdminGrants, department, DefaultRoles.DepartmentAdmin);

    /// <summary>The built-in role: everything at All, with or without a home department.</summary>
    public static Actor SystemAdmin(Guid? department = null) =>
        new(Guid.NewGuid(), DefaultRoles.SystemAdministrator, new RoleRef(Guid.NewGuid(), DefaultRoles.SystemAdministrator, true, true),
            PermissionCatalog.AllAtScopeAll, department, ActorType.User, Guid.NewGuid());

    /// <summary>A signed-in principal whose role has no grants at all.</summary>
    public static Actor Nobody(Guid? department) => With(new Dictionary<string, PermissionScope>(), department, "No role");
}

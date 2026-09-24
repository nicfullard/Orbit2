using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>The safety rules for roles as data (spec §6.5) and how role rows resolve into grants.</summary>
public class RoleRulesTests
{
    private static KeyValuePair<string, PermissionScope> Grant(string key, PermissionScope scope) => new(key, scope);

    [Fact]
    public void A_reserved_permission_is_refused_whatever_the_form_posts()
    {
        foreach (var key in new[] { Permission.UsersManage, Permission.RolesManage, Permission.ApiKeysManage })
        {
            var ex = Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant(key, PermissionScope.All)]));
            Assert.Contains("reserved", ex.Message);
        }
    }

    [Fact]
    public void An_unknown_key_is_refused()
    {
        Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant("tasks.close", PermissionScope.All)]));
        Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant("", PermissionScope.All)]));
    }

    [Fact]
    public void A_scope_the_permission_does_not_allow_is_refused()
    {
        Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant(Permission.SprintsManage, PermissionScope.Department)]));
        Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant(Permission.TasksCreate, PermissionScope.Own)]));
        Assert.Throws<ValidationException>(() => RoleRules.Validate([Grant(Permission.TasksView, (PermissionScope)42)]));
    }

    [Fact]
    public void None_entries_are_dropped_and_valid_grants_kept()
    {
        var grants = RoleRules.Validate([
            Grant(Permission.TasksView, PermissionScope.Department),
            Grant(Permission.TasksEdit, PermissionScope.None),
            Grant(Permission.SprintsManage, PermissionScope.All)
        ]);
        Assert.Equal(2, grants.Count);
        Assert.Equal(PermissionScope.Department, grants[Permission.TasksView]);
        Assert.Equal(PermissionScope.All, grants[Permission.SprintsManage]);
        Assert.False(grants.ContainsKey(Permission.TasksEdit));
    }

    [Fact]
    public void Requires_a_department_only_when_a_grant_is_at_department_scope()
    {
        Assert.True(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope> { [Permission.TasksView] = PermissionScope.Department }));
        Assert.False(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope> { [Permission.TasksView] = PermissionScope.All, [Permission.TasksEdit] = PermissionScope.Own }));
        Assert.False(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope>()));
    }

    [Fact]
    public void The_built_in_role_is_immutable_and_resolves_to_everything_at_all()
    {
        var builtIn = new ApplicationRole(DefaultRoles.SystemAdministrator) { IsBuiltIn = true };
        Assert.Throws<ValidationException>(() => RoleRules.RequireEditable(builtIn));
        RoleRules.RequireEditable(new ApplicationRole("Custom"));

        // No rows stored, yet every catalogue entry - so a permission added by a later release is held automatically.
        var resolved = RoleResolver.Resolve(builtIn);
        Assert.True(resolved.IsBuiltIn);
        Assert.Same(PermissionCatalog.AllAtScopeAll, resolved.Permissions);
        Assert.All(PermissionCatalog.All, p => Assert.Equal(PermissionScope.All, resolved.ScopeOf(p.Key)));
        Assert.False(resolved.RequiresDepartment);
        Assert.True(resolved.Ref.ReachesEverywhere);
    }

    [Fact]
    public void A_custom_role_resolves_to_exactly_its_rows()
    {
        var role = new ApplicationRole("Sprint manager");
        role.Permissions.Add(new RolePermission { Permission = Permission.TasksView, Scope = PermissionScope.Department });
        role.Permissions.Add(new RolePermission { Permission = Permission.SprintsManage, Scope = PermissionScope.All });
        role.Permissions.Add(new RolePermission { Permission = Permission.TasksEdit, Scope = PermissionScope.None });
        var resolved = RoleResolver.Resolve(role);
        Assert.Equal(2, resolved.Permissions.Count);
        Assert.Equal(PermissionScope.Department, resolved.ScopeOf(Permission.TasksView));
        Assert.Equal(PermissionScope.None, resolved.ScopeOf(Permission.TasksEdit));
        Assert.Equal(PermissionScope.None, resolved.ScopeOf(Permission.UsersManage));
        Assert.True(resolved.RequiresDepartment);
        Assert.True(resolved.Ref.ReachesEverywhere);
        Assert.False(resolved.Ref.IsBuiltIn);
    }

    [Fact]
    public void Names_and_descriptions_are_trimmed_and_bounded()
    {
        Assert.Equal("Auditor", RoleRules.ValidateName("  Auditor "));
        Assert.Throws<ValidationException>(() => RoleRules.ValidateName(" "));
        Assert.Throws<ValidationException>(() => RoleRules.ValidateName(new string('a', RoleRules.MaxNameLength + 1)));
        Assert.Null(RoleRules.ValidateDescription("  "));
        Assert.Throws<ValidationException>(() => RoleRules.ValidateDescription(new string('a', RoleRules.MaxDescriptionLength + 1)));
    }
}

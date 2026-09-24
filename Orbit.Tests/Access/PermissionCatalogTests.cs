using System.Reflection;
using System.Text.RegularExpressions;
using Orbit.Application;

namespace Orbit.Tests.Access;

/// <summary>The permission catalogue (spec §6.5) is consistent with the constants the code checks against.</summary>
public class PermissionCatalogTests
{
    private static IReadOnlyList<string> Constants => typeof(Permission)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!).ToList();

    [Fact]
    public void Every_constant_is_in_the_catalogue_and_vice_versa()
    {
        var keys = PermissionCatalog.All.Select(p => p.Key).ToList();
        Assert.Equal(Constants.OrderBy(k => k), keys.OrderBy(k => k));
        Assert.Equal(24, keys.Count);
    }

    [Fact]
    public void Keys_are_unique_lowercase_area_dot_action_and_short()
    {
        var keys = PermissionCatalog.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            Assert.Matches(new Regex("^[a-z_]+\\.[a-z_]+$"), key);
            Assert.True(key.Length <= 100, key);
        }
    }

    [Fact]
    public void Every_permission_allows_at_least_one_real_scope_and_has_a_label_and_group()
    {
        foreach (var p in PermissionCatalog.All)
        {
            Assert.NotEmpty(p.AllowedScopes);
            Assert.DoesNotContain(PermissionScope.None, p.AllowedScopes);
            Assert.False(string.IsNullOrWhiteSpace(p.Label), p.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.Description), p.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.Group), p.Key);
        }
    }

    [Fact]
    public void Reserved_permissions_are_exactly_users_roles_and_api_keys()
    {
        var reserved = PermissionCatalog.All.Where(p => p.SystemAdministratorOnly).Select(p => p.Key).OrderBy(k => k).ToList();
        Assert.Equal(new[] { Permission.ApiKeysManage, Permission.RolesManage, Permission.UsersManage }.OrderBy(k => k), reserved);
        Assert.All(PermissionCatalog.All.Where(p => p.SystemAdministratorOnly), p => Assert.True(p.AllOnly));
    }

    [Fact]
    public void Org_level_permissions_allow_only_all()
    {
        foreach (var key in new[] { Permission.SprintsManage, Permission.DepartmentsManage, Permission.CalendarManage, Permission.DirectoryManage, Permission.AgentsManage })
            Assert.True(PermissionCatalog.ByKey[key].AllOnly, key);
        Assert.False(PermissionCatalog.ByKey[Permission.TasksView].AllOnly);
        Assert.False(PermissionCatalog.ByKey[Permission.TasksCreate].Allows(PermissionScope.Own));
        Assert.True(PermissionCatalog.ByKey[Permission.TasksEdit].Allows(PermissionScope.Own));
    }

    /// <summary>AST-015: the five asset permissions and the scopes each allows (spec §6.19).</summary>
    [Fact]
    public void Asset_permissions_allow_the_scopes_the_spec_gives_them()
    {
        var own = new[] { PermissionScope.Own, PermissionScope.Department, PermissionScope.All };
        var dept = new[] { PermissionScope.Department, PermissionScope.All };
        Assert.Equal(own, PermissionCatalog.ByKey[Permission.AssetsView].AllowedScopes);
        Assert.Equal(dept, PermissionCatalog.ByKey[Permission.AssetsCreate].AllowedScopes);
        Assert.Equal(dept, PermissionCatalog.ByKey[Permission.AssetsEdit].AllowedScopes);
        Assert.Equal(own, PermissionCatalog.ByKey[Permission.AssetsCheck].AllowedScopes);
        Assert.Equal(dept, PermissionCatalog.ByKey[Permission.AssetsConfigure].AllowedScopes);
        Assert.All(PermissionCatalog.All.Where(p => p.Key.StartsWith("assets.")), p =>
        {
            Assert.Equal(PermissionCatalog.AssetsGroup, p.Group);
            Assert.False(p.SystemAdministratorOnly, p.Key);
        });
        // Types and locations belong to departments, so nothing about assets opens the Admin menu.
        Assert.DoesNotContain(PermissionCatalog.AdminPermissions, k => k.StartsWith("assets."));
    }

    [Fact]
    public void All_at_scope_all_covers_the_whole_catalogue()
    {
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.AllAtScopeAll.Count);
        Assert.All(PermissionCatalog.All, p => Assert.Equal(PermissionScope.All, PermissionCatalog.AllAtScopeAll[p.Key]));
        Assert.Null(PermissionCatalog.Find("tasks.close"));
    }

    [Fact]
    public void Admin_menu_permissions_are_catalogue_entries()
    {
        Assert.All(PermissionCatalog.AdminPermissions, key => Assert.NotNull(PermissionCatalog.Find(key)));
    }
}

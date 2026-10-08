using Orbit.Application;
using Orbit.Application.Services;

namespace Orbit.Tests.Access;

/// <summary>The navbar follows the actor's grants (spec §6.5, §6.9): a link only to a page the person can use.</summary>
public class NavigationTests
{
    private static readonly Guid Finance = Guid.NewGuid();

    private static List<string> Menu(Actor a) => Navigation.Visible(a).Select(i => i.Label).ToList();

    /// <summary>NAV-001: the shipped roles keep the menu they always had; the built-in role also gets every Admin item.</summary>
    [Fact]
    public void Shipped_roles_see_their_full_menus()
    {
        string[] full = ["Dashboard", "Today", "My Tasks", "Tasks", "Projects", "Backlog", "Sprints", "Recurring", "My Time", "Assets", "Requests"];
        Assert.Equal(full, Menu(TestActors.Member(Finance)));
        Assert.Equal(full, Menu(TestActors.DepartmentAdmin(Finance)));
        Assert.Equal([.. full, "Reports"], Menu(TestActors.SystemAdmin()));
        Assert.Equal(Navigation.Admin.Count, Navigation.AdminVisible(TestActors.SystemAdmin()).Count);
        Assert.Empty(Navigation.AdminVisible(TestActors.Member(Finance)));
        // A Department Admin configures the department's request flows, which are in the Admin menu.
        Assert.Equal(["Request flows"], Navigation.AdminVisible(TestActors.DepartmentAdmin(Finance)).Select(i => i.Label));
    }

    /// <summary>NAV-001: each Admin-menu permission on its own opens exactly one Admin item - the menu and the catalogue's list agree.</summary>
    [Fact]
    public void Each_admin_permission_opens_one_admin_item()
    {
        foreach (var key in PermissionCatalog.AdminPermissions)
        {
            var scope = PermissionCatalog.ByKey[key].AllowedScopes.Max();
            Assert.Single(Navigation.AdminVisible(TestActors.Grants(Finance, (key, scope))));
        }
        Assert.Equal(PermissionCatalog.AdminPermissions.Count, Navigation.Admin.Count);
    }

    /// <summary>NAV-002: each item needs its permission - here a requester, a view-only auditor and a role with nothing.</summary>
    [Fact]
    public void Items_follow_their_permissions()
    {
        Assert.Equal(["Dashboard", "Requests"], Menu(TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Own))));
        var auditor = TestActors.Grants(null, (Permission.TasksView, PermissionScope.All), (Permission.ProjectsView, PermissionScope.All));
        Assert.Equal(["Dashboard", "Tasks"], Menu(auditor));
        Assert.Equal(["Dashboard"], Menu(TestActors.Nobody(Finance)));
        // Configuring request flows and the action library are in the Admin menu, not the main one.
        var configurer = TestActors.Grants(Finance, (Permission.RequestsConfigure, PermissionScope.Department));
        Assert.Equal(["Dashboard"], Menu(configurer));
        Assert.Equal("/RequestCatalogue/Index", Navigation.AdminVisible(configurer).Single().Page);
        var scripter = TestActors.Grants(null, (Permission.ActionsCreate, PermissionScope.All));
        Assert.Equal(["Dashboard"], Menu(scripter));
        Assert.Equal("/Actions/Index", Navigation.AdminVisible(scripter).Single().Page);
    }

    /// <summary>NAV-003: taking one permission away takes its items - planning takes Backlog and Sprints, editing Today and My Tasks.</summary>
    [Fact]
    public void Removing_a_permission_removes_its_items()
    {
        var noPlan = new Dictionary<string, PermissionScope>(DefaultRoles.MemberGrants);
        noPlan.Remove(Permission.TasksPlan);
        var menu = Menu(TestActors.With(noPlan, Finance));
        Assert.DoesNotContain("Backlog", menu);
        Assert.DoesNotContain("Sprints", menu);
        Assert.Contains("Today", menu);

        var noEdit = new Dictionary<string, PermissionScope>(DefaultRoles.MemberGrants);
        noEdit.Remove(Permission.TasksEdit);
        noEdit.Remove(Permission.ProjectsEdit);
        noEdit.Remove(Permission.TimeLog);
        noEdit.Remove(Permission.TasksCreate);
        menu = Menu(TestActors.With(noEdit, Finance));
        Assert.DoesNotContain("Today", menu);
        Assert.DoesNotContain("My Tasks", menu);
        Assert.DoesNotContain("Projects", menu);
        Assert.DoesNotContain("My Time", menu);
        Assert.DoesNotContain("Recurring", menu);
        Assert.Contains("Tasks", menu);
        Assert.Contains("Dashboard", menu);
    }

    /// <summary>NAV-004: a view-only role gets the dashboard of its view scope, not the personal one it has no work on.</summary>
    [Fact]
    public void View_only_roles_get_their_view_scope_on_the_dashboard()
    {
        var viewer = TestActors.Grants(Finance, (Permission.TasksView, PermissionScope.Department));
        Assert.True(DashboardService.IsViewOnly(viewer));
        Assert.Equal(PermissionScope.Department, DashboardService.DashboardTier(viewer));
        Assert.False(DashboardService.IsViewOnly(TestActors.Member(Finance)));
        Assert.Equal(PermissionScope.Own, DashboardService.DashboardTier(TestActors.Member(Finance)));
        Assert.False(DashboardService.IsViewOnly(TestActors.Nobody(Finance)));
    }
}

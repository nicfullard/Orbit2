using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// The §6.5 capability table over the shipped roles' default grants: the migrated Member and Department Admin roles keep
/// exactly the rights they had, and the built-in System Administrator may do everything everywhere.
/// </summary>
public class DefaultRolesTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static readonly Actor Member = TestActors.Member(It);
    private static readonly Actor DeptAdmin = TestActors.DepartmentAdmin(It);
    private static readonly Actor SysAdmin = TestActors.SystemAdmin();

    private static TaskItem Task(Guid department, Guid? assignee = null, Guid? createdBy = null) =>
        new() { DepartmentId = department, AssigneeId = assignee, CreatedById = createdBy ?? Guid.NewGuid() };

    private static Project Project(Guid department, Guid? owner = null) => new() { DepartmentId = department, OwnerId = owner ?? Guid.NewGuid() };

    [Fact]
    public void Create_tasks_and_projects_in_own_department_or_anywhere()
    {
        Assert.True(AccessPolicy.CanCreateTaskIn(Member, It));
        Assert.False(AccessPolicy.CanCreateTaskIn(Member, Marketing));
        Assert.True(AccessPolicy.CanCreateProjectIn(DeptAdmin, It));
        Assert.False(AccessPolicy.CanCreateProjectIn(DeptAdmin, Marketing));
        Assert.True(AccessPolicy.CanCreateTaskIn(SysAdmin, Marketing));
        Assert.True(AccessPolicy.CanCreateProjectIn(SysAdmin, Marketing));
    }

    [Fact]
    public void Edit_tasks_own_or_assigned_for_a_member_any_in_department_for_an_admin_any_for_system_admin()
    {
        var colleagues = Task(It, assignee: Guid.NewGuid());
        Assert.False(AccessPolicy.CanEditTask(Member, colleagues));
        Assert.True(AccessPolicy.CanEditTask(Member, Task(It, assignee: Member.UserId)));
        Assert.True(AccessPolicy.CanEditTask(Member, Task(It, createdBy: Member.UserId)));
        Assert.True(AccessPolicy.CanEditTask(DeptAdmin, colleagues));
        Assert.False(AccessPolicy.CanEditTask(DeptAdmin, Task(Marketing)));
        Assert.True(AccessPolicy.CanEditTask(SysAdmin, Task(Marketing)));
    }

    [Fact]
    public void Status_changes_follow_edit_rights()
    {
        var own = Task(It, assignee: Member.UserId);
        Assert.True(AccessPolicy.CanChangeStatus(Member, own));
        Assert.False(AccessPolicy.CanChangeStatus(Member, Task(It, assignee: Guid.NewGuid())));
        Assert.True(AccessPolicy.CanChangeStatus(DeptAdmin, Task(It)));
        Assert.True(AccessPolicy.CanChangeStatus(SysAdmin, Task(Marketing)));
    }

    [Fact]
    public void Take_and_plan_reach_the_whole_department()
    {
        Assert.True(AccessPolicy.CanTakeTask(Member, Task(It)));
        Assert.True(AccessPolicy.CanPlanTask(Member, Task(It, assignee: Guid.NewGuid())));
        Assert.False(AccessPolicy.CanPlanTask(Member, Task(Marketing)));
        Assert.True(AccessPolicy.CanPlanTask(DeptAdmin, Task(It)));
        Assert.True(AccessPolicy.CanPlanTask(SysAdmin, Task(Marketing)));
    }

    [Fact]
    public void Projects_view_department_and_shared_edit_owned_or_department()
    {
        var theirs = Project(It);
        Assert.True(AccessPolicy.CanViewProject(Member, theirs));
        Assert.True(AccessPolicy.CanViewProject(Member, Project(Marketing), hasTasksInActorDepartment: true));
        Assert.False(AccessPolicy.CanViewProject(Member, Project(Marketing)));
        Assert.False(AccessPolicy.CanEditProject(Member, theirs));
        Assert.True(AccessPolicy.CanEditProject(Member, Project(It, owner: Member.UserId)));
        Assert.True(AccessPolicy.CanEditProject(DeptAdmin, theirs));
        Assert.False(AccessPolicy.CanEditProject(DeptAdmin, Project(Marketing)));
        Assert.True(AccessPolicy.CanEditProject(SysAdmin, Project(Marketing)));
        Assert.True(AccessPolicy.CanRunCriticalPath(Member, Project(It, owner: Member.UserId)));
        Assert.False(AccessPolicy.CanRunCriticalPath(Member, theirs));
    }

    [Fact]
    public void Filing_under_another_departments_project_is_system_admin_only()
    {
        Assert.False(AccessPolicy.CanFileCrossDepartmentTask(Member));
        Assert.False(AccessPolicy.CanFileCrossDepartmentTask(DeptAdmin));
        Assert.True(AccessPolicy.CanFileCrossDepartmentTask(SysAdmin));
        Assert.True(AccessPolicy.CanAddTaskToProject(Member, Project(It)));
        Assert.False(AccessPolicy.CanAddTaskToProject(Member, Project(Marketing)));
    }

    [Fact]
    public void Time_own_entries_on_assigned_tasks_department_admins_for_anyone_in_department()
    {
        var mine = Task(It, assignee: Member.UserId);
        var colleague = Guid.NewGuid();
        Assert.True(AccessPolicy.CanLogTimeFor(Member, mine, Member.UserId!.Value));
        Assert.False(AccessPolicy.CanLogTimeFor(Member, Task(It, assignee: colleague), Member.UserId!.Value));
        Assert.False(AccessPolicy.CanLogTimeFor(Member, mine, colleague));
        Assert.True(AccessPolicy.CanLogTimeFor(DeptAdmin, Task(It, assignee: colleague), colleague));
        Assert.True(AccessPolicy.CanLogTimeFor(DeptAdmin, Task(It), DeptAdmin.UserId!.Value));
        Assert.False(AccessPolicy.CanLogTimeFor(DeptAdmin, Task(Marketing, assignee: colleague), colleague));
        Assert.True(AccessPolicy.CanLogTimeFor(SysAdmin, Task(Marketing, assignee: colleague), colleague));
        Assert.False(AccessPolicy.CanLogTimeForOthers(Member, mine));
        Assert.True(AccessPolicy.CanLogTimeForOthers(DeptAdmin, mine));

        var entry = new TimeEntry { UserId = Member.UserId!.Value };
        Assert.True(AccessPolicy.CanEditTimeEntry(Member, entry, mine));
        Assert.False(AccessPolicy.CanEditTimeEntry(Member, new TimeEntry { UserId = colleague }, mine));
        Assert.True(AccessPolicy.CanEditTimeEntry(DeptAdmin, new TimeEntry { UserId = colleague }, mine));
    }

    [Fact]
    public void Attachments_follow_view_and_edit_rights()
    {
        Assert.True(AccessPolicy.CanAttachToTask(Member, Task(It, assignee: Guid.NewGuid())));
        Assert.False(AccessPolicy.CanAttachToTask(Member, Task(Marketing)));
        Assert.True(AccessPolicy.CanAttachToProject(Member, Project(It)));
        Assert.False(AccessPolicy.CanAttachToProject(Member, Project(Marketing)));
        Assert.True(AccessPolicy.CanAttachToProject(SysAdmin, Project(Marketing)));
    }

    [Fact]
    public void Organisation_level_permissions_are_system_admin_only_by_default()
    {
        foreach (var actor in new[] { Member, DeptAdmin })
        {
            Assert.False(AccessPolicy.CanManageSprints(actor));
            Assert.False(AccessPolicy.CanManageWorkingCalendar(actor));
            Assert.False(AccessPolicy.CanManageUsers(actor));
            Assert.False(AccessPolicy.CanManageRoles(actor));
            Assert.False(AccessPolicy.CanManageApiKeys(actor));
            Assert.False(AccessPolicy.CanManageDepartments(actor));
            Assert.False(AccessPolicy.CanManageDirectory(actor));
            Assert.False(AccessPolicy.CanManageAgents(actor));
            Assert.False(AccessPolicy.CanViewReports(actor));
            Assert.False(AccessPolicy.CanViewAuditLog(actor));
        }
        Assert.True(AccessPolicy.CanManageSprints(SysAdmin));
        Assert.True(AccessPolicy.CanViewReports(SysAdmin));
        Assert.True(AccessPolicy.CanViewAuditLog(SysAdmin));
    }

    [Fact]
    public void Migrated_roles_need_a_department_the_built_in_role_does_not()
    {
        Assert.True(Member.RequiresDepartment);
        Assert.True(DeptAdmin.RequiresDepartment);
        Assert.False(SysAdmin.RequiresDepartment);
        Assert.True(RoleRules.RequiresDepartment(DefaultRoles.MemberGrants));
        Assert.True(RoleRules.RequiresDepartment(DefaultRoles.DepartmentAdminGrants));
    }

    [Fact]
    public void Default_grants_are_valid_against_the_catalogue()
    {
        Assert.Equal(DefaultRoles.MemberGrants.Count, RoleRules.Validate(DefaultRoles.MemberGrants).Count);
        Assert.Equal(DefaultRoles.DepartmentAdminGrants.Count, RoleRules.Validate(DefaultRoles.DepartmentAdminGrants).Count);
    }

    [Fact]
    public void Dashboard_tiers_personal_for_member_department_for_admin_company_for_system_admin()
    {
        Assert.Equal(PermissionScope.Own, DashboardService.DashboardTier(Member));
        Assert.Equal(PermissionScope.Department, DashboardService.DashboardTier(DeptAdmin));
        Assert.Equal(PermissionScope.All, DashboardService.DashboardTier(SysAdmin));
        // The spec's worked examples: an Auditor (view everything, edit nothing) lands on the company tier,
        // a Coordinator (Member plus tasks.edit at Department) on the department tier.
        var auditor = TestActors.Grants(null, (Permission.TasksView, PermissionScope.All), (Permission.ProjectsView, PermissionScope.All));
        Assert.Equal(PermissionScope.All, DashboardService.DashboardTier(auditor));
        var coordinatorGrants = new Dictionary<string, PermissionScope>(DefaultRoles.MemberGrants) { [Permission.TasksEdit] = PermissionScope.Department };
        Assert.Equal(PermissionScope.Department, DashboardService.DashboardTier(TestActors.With(coordinatorGrants, It)));
        Assert.Equal(PermissionScope.None, DashboardService.DashboardTier(TestActors.Nobody(It)));
    }

    [Fact]
    public void Role_badges_colour_on_reach()
    {
        Assert.True(SysAdmin.Role.IsBuiltIn);
        Assert.True(SysAdmin.Role.ReachesEverywhere);
        Assert.False(DeptAdmin.Role.ReachesEverywhere);
        Assert.False(Member.Role.ReachesEverywhere);
        Assert.True(TestActors.Grants(It, (Permission.SprintsManage, PermissionScope.All)).Role.ReachesEverywhere);
    }
}

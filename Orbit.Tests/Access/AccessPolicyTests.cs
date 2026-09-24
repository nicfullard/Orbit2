using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// The §6.5 rules for taking an unassigned task and for what a scope reaches: who may take one, that being allowed to take
/// a task never widens what a Member may otherwise see or edit, and how Own / Department / All grants resolve against objects.
/// </summary>
public class AccessPolicyTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static TaskItem Task(Guid department, Guid? assignee = null, TaskItemStatus status = TaskItemStatus.Todo, Guid? createdBy = null) =>
        new() { DepartmentId = department, AssigneeId = assignee, Status = status, CreatedById = createdBy ?? Guid.NewGuid() };

    [Theory]
    [InlineData(TaskItemStatus.Todo)]
    [InlineData(TaskItemStatus.InProgress)]
    [InlineData(TaskItemStatus.Blocked)]
    public void Member_may_take_an_open_unassigned_task_in_own_department(TaskItemStatus status)
    {
        var member = TestActors.Member(It);
        Assert.True(AccessPolicy.CanTakeTask(member, Task(It, status: status)));
    }

    [Fact]
    public void Member_may_not_take_a_task_from_another_department()
    {
        var member = TestActors.Member(It);
        Assert.False(AccessPolicy.CanTakeTask(member, Task(Marketing)));
    }

    [Fact]
    public void An_assigned_task_cannot_be_taken_even_by_an_admin()
    {
        var task = Task(It, assignee: Guid.NewGuid());
        Assert.False(AccessPolicy.CanTakeTask(TestActors.Member(It), task));
        Assert.False(AccessPolicy.CanTakeTask(TestActors.DepartmentAdmin(It), task));
        Assert.False(AccessPolicy.CanTakeTask(TestActors.SystemAdmin(), task));
    }

    [Theory]
    [InlineData(TaskItemStatus.Done)]
    [InlineData(TaskItemStatus.Cancelled)]
    public void A_closed_task_cannot_be_taken(TaskItemStatus status)
    {
        Assert.False(AccessPolicy.CanTakeTask(TestActors.Member(It), Task(It, status: status)));
        Assert.False(AccessPolicy.CanTakeTask(TestActors.SystemAdmin(), Task(It, status: status)));
    }

    [Fact]
    public void Admins_may_take_within_their_reach()
    {
        Assert.True(AccessPolicy.CanTakeTask(TestActors.DepartmentAdmin(It), Task(It)));
        Assert.False(AccessPolicy.CanTakeTask(TestActors.DepartmentAdmin(It), Task(Marketing)));
        Assert.True(AccessPolicy.CanTakeTask(TestActors.SystemAdmin(), Task(Marketing)));
    }

    [Fact]
    public void Taking_needs_the_take_permission_not_edit_rights()
    {
        // A role that sees and edits its department but was never granted tasks.take can't take; one with only tasks.take can.
        var editor = TestActors.Grants(It, (Permission.TasksView, PermissionScope.Department), (Permission.TasksEdit, PermissionScope.Department));
        Assert.False(AccessPolicy.CanTakeTask(editor, Task(It)));
        var taker = TestActors.Grants(It, (Permission.TasksTake, PermissionScope.Department));
        Assert.True(AccessPolicy.CanTakeTask(taker, Task(It)));
    }

    [Fact]
    public void A_principal_without_a_user_cannot_take()
    {
        // Background jobs run as the System actor, which has no user to assign the task to.
        Assert.False(AccessPolicy.CanTakeTask(Actor.System, Task(It)));
    }

    [Fact]
    public void Taking_does_not_grant_edit_rights_until_the_task_is_theirs()
    {
        var member = TestActors.Member(It);
        var task = Task(It);

        Assert.True(AccessPolicy.CanTakeTask(member, task));
        Assert.False(AccessPolicy.CanEditTask(member, task));

        task.AssigneeId = member.UserId;
        Assert.False(AccessPolicy.CanTakeTask(member, task));
        Assert.True(AccessPolicy.CanEditTask(member, task));
    }

    [Fact]
    public void Member_sees_every_task_in_own_department_but_not_beyond()
    {
        var member = TestActors.Member(It);
        Assert.True(AccessPolicy.CanViewTask(member, Task(It, assignee: Guid.NewGuid())));
        Assert.True(AccessPolicy.CanViewTask(member, Task(It)));
        Assert.False(AccessPolicy.CanViewTask(member, Task(Marketing)));
    }

    [Fact]
    public void Own_scope_follows_the_task_not_the_department()
    {
        // A department-less user whose tasks.view / tasks.edit are at Own reaches their own tasks anywhere and nothing else.
        var me = TestActors.Grants(null, (Permission.TasksView, PermissionScope.Own), (Permission.TasksEdit, PermissionScope.Own));
        Assert.True(AccessPolicy.CanViewTask(me, Task(Marketing, assignee: me.UserId)));
        Assert.True(AccessPolicy.CanEditTask(me, Task(It, createdBy: me.UserId)));
        Assert.False(AccessPolicy.CanViewTask(me, Task(It, assignee: Guid.NewGuid())));
        Assert.False(AccessPolicy.CanEditTask(me, Task(It)));
    }

    [Fact]
    public void A_role_with_no_grants_sees_nothing()
    {
        var nobody = TestActors.Nobody(It);
        Assert.False(AccessPolicy.CanViewTask(nobody, Task(It, assignee: nobody.UserId)));
        Assert.False(AccessPolicy.CanEditTask(nobody, Task(It, assignee: nobody.UserId)));
        Assert.False(AccessPolicy.CanTakeTask(nobody, Task(It)));
        Assert.False(AccessPolicy.CanViewReports(nobody));
        Assert.False(AccessPolicy.CanManageSprints(nobody));
    }

    [Fact]
    public void All_scope_reaches_every_department_and_department_scope_only_its_own()
    {
        var everywhere = TestActors.Grants(It, (Permission.TasksEdit, PermissionScope.All));
        Assert.True(AccessPolicy.CanEditTask(everywhere, Task(Marketing, assignee: Guid.NewGuid())));

        var coordinator = TestActors.Grants(It, (Permission.TasksEdit, PermissionScope.Department));
        Assert.True(AccessPolicy.CanEditTask(coordinator, Task(It, assignee: Guid.NewGuid())));
        Assert.False(AccessPolicy.CanEditTask(coordinator, Task(Marketing, assignee: Guid.NewGuid())));
        // A grant covers the scopes below it, and Own is independent of department: their own task anywhere stays theirs.
        Assert.True(AccessPolicy.CanEditTask(coordinator, Task(Marketing, assignee: coordinator.UserId)));
    }

    [Fact]
    public void Cross_department_filing_and_moving_projects_need_all_scope()
    {
        Assert.False(AccessPolicy.CanFileCrossDepartmentTask(TestActors.DepartmentAdmin(It)));
        Assert.True(AccessPolicy.CanFileCrossDepartmentTask(TestActors.Grants(It, (Permission.TasksCreate, PermissionScope.All))));
        Assert.False(AccessPolicy.CanMoveProject(TestActors.DepartmentAdmin(It)));
        Assert.True(AccessPolicy.CanMoveProject(TestActors.SystemAdmin()));
    }

    [Fact]
    public void Shared_projects_are_visible_at_department_scope_but_not_at_own()
    {
        var project = new Project { DepartmentId = Marketing, OwnerId = Guid.NewGuid() };
        Assert.True(AccessPolicy.CanViewProject(TestActors.Member(It), project, hasTasksInActorDepartment: true));
        Assert.False(AccessPolicy.CanViewProject(TestActors.Member(It), project));
        var ownOnly = TestActors.Grants(It, (Permission.ProjectsView, PermissionScope.Own));
        Assert.False(AccessPolicy.CanViewProject(ownOnly, project, hasTasksInActorDepartment: true));
        Assert.True(AccessPolicy.CanViewProject(ownOnly, new Project { DepartmentId = Marketing, OwnerId = ownOnly.UserId!.Value }));
    }

    [Fact]
    public void The_built_in_role_holds_every_permission_including_reserved_ones()
    {
        var admin = TestActors.SystemAdmin();
        foreach (var p in PermissionCatalog.All)
            Assert.True(admin.CanAnywhere(p.Key), p.Key);
        Assert.True(AccessPolicy.CanManageUsers(admin));
        Assert.True(AccessPolicy.CanManageRoles(admin));
        Assert.True(AccessPolicy.CanManageApiKeys(admin));
        Assert.False(admin.RequiresDepartment);
    }
}

using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// The §6.5 capability table for taking an unassigned task: who may take one, and that being allowed to take a task
/// never widens what a Member may otherwise see or edit.
/// </summary>
public class AccessPolicyTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static Actor User(OrbitRole role, Guid? department) =>
        new(Guid.NewGuid(), role.ToString(), role, department, ActorType.User, Guid.NewGuid());

    private static TaskItem Task(Guid department, Guid? assignee = null, TaskItemStatus status = TaskItemStatus.Todo) =>
        new() { DepartmentId = department, AssigneeId = assignee, Status = status, CreatedById = Guid.NewGuid() };

    [Theory]
    [InlineData(TaskItemStatus.Todo)]
    [InlineData(TaskItemStatus.InProgress)]
    [InlineData(TaskItemStatus.Blocked)]
    public void Member_may_take_an_open_unassigned_task_in_own_department(TaskItemStatus status)
    {
        var member = User(OrbitRole.Member, It);
        Assert.True(AccessPolicy.CanTakeTask(member, Task(It, status: status)));
    }

    [Fact]
    public void Member_may_not_take_a_task_from_another_department()
    {
        var member = User(OrbitRole.Member, It);
        Assert.False(AccessPolicy.CanTakeTask(member, Task(Marketing)));
    }

    [Fact]
    public void An_assigned_task_cannot_be_taken_even_by_an_admin()
    {
        var task = Task(It, assignee: Guid.NewGuid());
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.Member, It), task));
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.DepartmentAdmin, It), task));
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.SystemAdmin, null), task));
    }

    [Theory]
    [InlineData(TaskItemStatus.Done)]
    [InlineData(TaskItemStatus.Cancelled)]
    public void A_closed_task_cannot_be_taken(TaskItemStatus status)
    {
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.Member, It), Task(It, status: status)));
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.SystemAdmin, null), Task(It, status: status)));
    }

    [Fact]
    public void Admins_may_take_within_their_reach()
    {
        Assert.True(AccessPolicy.CanTakeTask(User(OrbitRole.DepartmentAdmin, It), Task(It)));
        Assert.False(AccessPolicy.CanTakeTask(User(OrbitRole.DepartmentAdmin, It), Task(Marketing)));
        Assert.True(AccessPolicy.CanTakeTask(User(OrbitRole.SystemAdmin, null), Task(Marketing)));
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
        var member = User(OrbitRole.Member, It);
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
        var member = User(OrbitRole.Member, It);
        Assert.True(AccessPolicy.CanViewTask(member, Task(It, assignee: Guid.NewGuid())));
        Assert.True(AccessPolicy.CanViewTask(member, Task(It)));
        Assert.False(AccessPolicy.CanViewTask(member, Task(Marketing)));
    }
}

using Orbit.Application;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Tests.Access;

/// <summary>
/// Task status rules (spec §6.2, §6.5): every status - including Done/Cancelled and reopening - is open to whoever may edit
/// the task (a Member on their own or assigned tasks, an admin within their reach), and Waiting is an open, started status.
/// </summary>
public class StatusTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static readonly TaskItemStatus[] AllStatuses =
        [TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked, TaskItemStatus.Done, TaskItemStatus.Cancelled];

    private static Actor User(OrbitRole role, Guid? department) =>
        new(Guid.NewGuid(), role.ToString(), role, department, ActorType.User, Guid.NewGuid());

    private static TaskItem Task(Guid department, TaskItemStatus status = TaskItemStatus.Todo, Guid? assignee = null, Guid? createdBy = null) =>
        new() { DepartmentId = department, Status = status, AssigneeId = assignee, CreatedById = createdBy ?? Guid.NewGuid() };

    [Fact]
    public void Waiting_is_open_and_sits_between_in_progress_and_blocked()
    {
        Assert.False(TaskItemStatus.Waiting.IsClosed());
        Assert.Equal("Waiting", TaskItemStatus.Waiting.Label());
        Assert.Equal(AllStatuses, Enum.GetValues<TaskItemStatus>());
    }

    [Theory]
    [InlineData(TaskItemStatus.Todo)]
    [InlineData(TaskItemStatus.InProgress)]
    [InlineData(TaskItemStatus.Waiting)]
    [InlineData(TaskItemStatus.Blocked)]
    [InlineData(TaskItemStatus.Done)]
    [InlineData(TaskItemStatus.Cancelled)]
    public void An_assignee_may_set_any_status_including_close_and_reopen(TaskItemStatus current)
    {
        var member = User(OrbitRole.Member, It);
        var task = Task(It, current, assignee: member.UserId);

        Assert.True(AccessPolicy.CanChangeStatus(member, task));
        Assert.Equal(AllStatuses, Ui.AllowedStatuses(member, task));
    }

    [Fact]
    public void The_creator_may_close_and_reopen_their_task()
    {
        var member = User(OrbitRole.Member, It);
        Assert.True(AccessPolicy.CanChangeStatus(member, Task(It, TaskItemStatus.InProgress, createdBy: member.UserId)));
        Assert.True(AccessPolicy.CanChangeStatus(member, Task(It, TaskItemStatus.Done, createdBy: member.UserId)));
    }

    [Fact]
    public void A_member_cannot_change_status_of_a_colleagues_task()
    {
        var member = User(OrbitRole.Member, It);
        var colleagues = Task(It, TaskItemStatus.InProgress, assignee: Guid.NewGuid());

        Assert.False(AccessPolicy.CanChangeStatus(member, colleagues));
        Assert.Equal(new[] { TaskItemStatus.InProgress }, Ui.AllowedStatuses(member, colleagues));
    }

    [Fact]
    public void An_unassigned_task_is_taken_first_and_then_its_status_opens_up()
    {
        var member = User(OrbitRole.Member, It);
        var task = Task(It);

        Assert.False(AccessPolicy.CanChangeStatus(member, task));
        Assert.True(AccessPolicy.CanTakeTask(member, task));

        task.AssigneeId = member.UserId;
        Assert.True(AccessPolicy.CanChangeStatus(member, task));
    }

    [Fact]
    public void Admins_may_change_status_within_their_reach()
    {
        var somebodyElses = Task(It, TaskItemStatus.Done, assignee: Guid.NewGuid());

        Assert.True(AccessPolicy.CanChangeStatus(User(OrbitRole.DepartmentAdmin, It), somebodyElses));
        Assert.False(AccessPolicy.CanChangeStatus(User(OrbitRole.DepartmentAdmin, Marketing), somebodyElses));
        Assert.True(AccessPolicy.CanChangeStatus(User(OrbitRole.SystemAdmin, null), somebodyElses));
    }

    [Fact]
    public void Waiting_counts_as_started_for_dependency_gating()
    {
        // Like Blocked: leaving Todo for Waiting is the successor's start, and a Waiting predecessor has started but not finished.
        Assert.True(DependencyRules.IsStart(TaskItemStatus.Todo, TaskItemStatus.Waiting));
        Assert.True(DependencyRules.HasStarted(TaskItemStatus.Waiting));
        Assert.False(DependencyRules.HasFinished(TaskItemStatus.Waiting));
        Assert.True(DependencyRules.IsMet(DependencyType.StartToStart, TaskItemStatus.Waiting));
        Assert.False(DependencyRules.IsMet(DependencyType.FinishToStart, TaskItemStatus.Waiting));
    }
}

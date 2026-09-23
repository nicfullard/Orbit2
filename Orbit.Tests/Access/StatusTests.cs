using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>The Waiting status (spec §6.2): an open, started status that anyone in the department may use, distinct from Blocked.</summary>
public class StatusTests
{
    private static readonly Guid It = Guid.NewGuid();

    private static Actor Member() => new(Guid.NewGuid(), "member", OrbitRole.Member, It, ActorType.User, Guid.NewGuid());

    private static TaskItem Task(TaskItemStatus status) => new() { DepartmentId = It, Status = status };

    [Fact]
    public void Waiting_is_open_and_sits_between_in_progress_and_blocked()
    {
        Assert.False(TaskItemStatus.Waiting.IsClosed());
        Assert.Equal("Waiting", TaskItemStatus.Waiting.Label());
        Assert.Equal([TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked, TaskItemStatus.Done, TaskItemStatus.Cancelled],
            Enum.GetValues<TaskItemStatus>());
    }

    [Theory]
    [InlineData(TaskItemStatus.Todo, TaskItemStatus.Waiting)]
    [InlineData(TaskItemStatus.InProgress, TaskItemStatus.Waiting)]
    [InlineData(TaskItemStatus.Waiting, TaskItemStatus.InProgress)]
    [InlineData(TaskItemStatus.Waiting, TaskItemStatus.Blocked)]
    [InlineData(TaskItemStatus.Waiting, TaskItemStatus.Todo)]
    public void A_member_may_move_into_and_out_of_waiting(TaskItemStatus from, TaskItemStatus to)
    {
        Assert.True(AccessPolicy.CanChangeStatus(Member(), Task(from), to));
    }

    [Fact]
    public void A_member_still_cannot_close_a_waiting_task()
    {
        Assert.False(AccessPolicy.CanChangeStatus(Member(), Task(TaskItemStatus.Waiting), TaskItemStatus.Done));
        Assert.False(AccessPolicy.CanChangeStatus(Member(), Task(TaskItemStatus.Waiting), TaskItemStatus.Cancelled));
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

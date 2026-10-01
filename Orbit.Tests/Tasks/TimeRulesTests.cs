using Orbit.Application;
using Orbit.Data.Entities;
using Orbit.Tests.Access;

namespace Orbit.Tests.Tasks;

/// <summary>Setting your own task Done without having logged time on it asks first (spec §6.10, §13 item 63).</summary>
public class TimeRulesTests
{
    private static readonly Guid It = Guid.NewGuid();

    private static TaskItem Task(Guid? assignee, TaskItemStatus status = TaskItemStatus.InProgress) =>
        new() { DepartmentId = It, AssigneeId = assignee, Status = status };

    /// <summary>TIME-001: the assignee is asked when they have logged no time and have no clock running on the open task.</summary>
    [Fact]
    public void The_assignee_without_time_is_asked()
    {
        var member = TestActors.Member(It);
        Assert.True(TimeRules.AskBeforeDoneWithoutTime(member, Task(member.UserId), hasLoggedTime: false, clockRunning: false));
        Assert.True(TimeRules.AskBeforeDoneWithoutTime(member, Task(member.UserId, TaskItemStatus.Todo), hasLoggedTime: false, clockRunning: false));
    }

    /// <summary>TIME-002: not once they have logged time on it, nor while their clock runs on it (stopping it logs the time).</summary>
    [Fact]
    public void Logged_time_or_a_running_clock_means_no_question()
    {
        var member = TestActors.Member(It);
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(member, Task(member.UserId), hasLoggedTime: true, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(member, Task(member.UserId), hasLoggedTime: false, clockRunning: true));
    }

    /// <summary>TIME-003: only the assignee is asked - not on someone else's or an unassigned task, even by an admin - and not on a task already Done.</summary>
    [Fact]
    public void Only_the_assignee_of_an_unfinished_task_is_asked()
    {
        var member = TestActors.Member(It);
        var admin = TestActors.DepartmentAdmin(It);
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(member, Task(Guid.NewGuid()), hasLoggedTime: false, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(member, Task(null), hasLoggedTime: false, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(admin, Task(member.UserId), hasLoggedTime: false, clockRunning: false));
        Assert.True(TimeRules.AskBeforeDoneWithoutTime(admin, Task(admin.UserId), hasLoggedTime: false, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(member, Task(member.UserId, TaskItemStatus.Done), hasLoggedTime: false, clockRunning: false));
    }

    /// <summary>TIME-004: nobody is asked to log time they can't log - a role without Log time, or an actor with no user.</summary>
    [Fact]
    public void An_actor_who_cannot_log_time_is_not_asked()
    {
        var noTimeLog = TestActors.Grants(It, (Permission.TasksView, PermissionScope.Own), (Permission.TasksEdit, PermissionScope.Own));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(noTimeLog, Task(noTimeLog.UserId), hasLoggedTime: false, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(Actor.System, Task(null), hasLoggedTime: false, clockRunning: false));
    }
}

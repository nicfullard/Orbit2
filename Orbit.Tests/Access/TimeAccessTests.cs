using Orbit.Application;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// The rule the MCP <c>log_time</c> tool relies on (spec §6.10, §7.1): an API key acts as the synthetic Claude user and
/// always logs for a named person, so it takes the on-behalf branch of <see cref="AccessPolicy.CanLogTimeFor"/> -
/// Log time at Department scope for the task's department, or All.
/// </summary>
public class TimeAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private static Actor Key(IReadOnlyDictionary<string, PermissionScope> grants) =>
        new(WellKnownIds.ClaudeAgentUserId, WellKnownIds.ClaudeAgentDisplayName,
            new RoleRef(Guid.NewGuid(), "Key role", false, grants.Values.Any(s => s == PermissionScope.All)),
            grants, It, ActorType.Api, Guid.NewGuid());

    private static Actor KeyWithTimeLog(PermissionScope scope) =>
        Key(new Dictionary<string, PermissionScope> { [Permission.TasksView] = scope, [Permission.TimeLog] = scope });

    private static TaskItem Task(Guid department, Guid? assignee = null) => new() { DepartmentId = department, AssigneeId = assignee };

    [Fact]
    public void A_department_scoped_key_logs_for_a_person_on_its_own_departments_tasks_only()
    {
        var key = KeyWithTimeLog(PermissionScope.Department);
        Assert.True(AccessPolicy.CanLogTimeFor(key, Task(It, assignee: Bob), Bob));
        Assert.True(AccessPolicy.CanLogTimeFor(key, Task(It), Bob));
        Assert.False(AccessPolicy.CanLogTimeFor(key, Task(Marketing, assignee: Bob), Bob));
    }

    [Fact]
    public void An_all_scoped_key_logs_for_a_person_anywhere()
    {
        Assert.True(AccessPolicy.CanLogTimeFor(KeyWithTimeLog(PermissionScope.All), Task(Marketing, assignee: Bob), Bob));
    }

    [Fact]
    public void A_key_on_the_shipped_member_grants_cannot_log_for_anyone_even_the_assignee()
    {
        var key = Key(DefaultRoles.MemberGrants);
        Assert.False(AccessPolicy.CanLogTimeFor(key, Task(It, assignee: Bob), Bob));
        Assert.False(AccessPolicy.CanLogTimeFor(key, Task(It), Bob));
    }
}

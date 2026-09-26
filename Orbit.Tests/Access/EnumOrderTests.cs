using System.Linq.Expressions;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// Sorting by an enum (spec §13 decision 47): enums are stored as their names, so a database sort on the column is alphabetical.
/// The <see cref="EnumOrder"/> keys rank by declaration order instead, as a CASE the database can run.
/// </summary>
public class EnumOrderTests
{
    private static IEnumerable<T> Sorted<T>(IEnumerable<T> items, Expression<Func<T, int>> key, bool descending = false) =>
        descending ? items.OrderByDescending(key.Compile()) : items.OrderBy(key.Compile());

    /// <summary>ORD-001: priority descending puts Critical first - the alphabetical order put it last.</summary>
    [Fact]
    public void Task_priority_ranks_low_to_critical()
    {
        Assert.Equal(["Medium", "Low", "High", "Critical"], Enum.GetNames<TaskPriority>().OrderDescending(StringComparer.Ordinal).ToArray());

        var tasks = Enum.GetValues<TaskPriority>().Reverse().Select(p => new TaskItem { Priority = p }).ToList();
        Assert.Equal([TaskPriority.Critical, TaskPriority.High, TaskPriority.Medium, TaskPriority.Low],
            Sorted(tasks, EnumOrder.ByTaskPriority, descending: true).Select(t => t.Priority).ToArray());
    }

    /// <summary>ORD-002: statuses sort open first, in their workflow order, then Done and Cancelled.</summary>
    [Fact]
    public void Task_status_ranks_in_workflow_order()
    {
        var tasks = Enum.GetValues<TaskItemStatus>().Reverse().Select(s => new TaskItem { Status = s }).ToList();
        Assert.Equal(
            [TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked, TaskItemStatus.Done, TaskItemStatus.Cancelled],
            Sorted(tasks, EnumOrder.ByTaskStatus).Select(t => t.Status).ToArray());
    }

    /// <summary>ORD-003: projects sort Active, On hold, Completed, Archived - not Active, Archived, Completed, On hold.</summary>
    [Fact]
    public void Project_status_ranks_active_to_archived()
    {
        var projects = Enum.GetValues<ProjectStatus>().Reverse().Select(s => new Project { Status = s }).ToList();
        Assert.Equal([ProjectStatus.Active, ProjectStatus.OnHold, ProjectStatus.Completed, ProjectStatus.Archived],
            Sorted(projects, EnumOrder.ByProjectStatus).Select(p => p.Status).ToArray());
    }

    /// <summary>ORD-004: every value of any enum gets its declaration position, and the key is a plain conditional the database can translate.</summary>
    [Fact]
    public void Rank_is_the_declaration_position_as_a_conditional()
    {
        var rank = EnumOrder.Rank((AssetCheck c) => c.Outcome);
        var compiled = rank.Compile();
        var values = Enum.GetValues<AssetCheckOutcome>();
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(i, compiled(new AssetCheck { Outcome = values[i] }));

        Assert.IsAssignableFrom<ConditionalExpression>(EnumOrder.ByTaskPriority.Body);
        Assert.DoesNotContain("Convert", EnumOrder.ByTaskPriority.Body.ToString());
    }
}

using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Time by person and estimate accuracy (spec §12).</summary>
public class TimeReportRulesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [Alice] = "Alice", [Bob] = "Bob" };

    private static string Name(Guid? id) => id is Guid g && Names.TryGetValue(g, out var n) ? n : "Unassigned";

    private static int _number;

    private static TaskTimeFacts Task(int? estimate, int total, TaskItemStatus status = TaskItemStatus.InProgress, Guid? assignee = null) =>
        new(Guid.NewGuid(), $"T-26-{Interlocked.Increment(ref _number):00000}", "Task", status, assignee, estimate, total);

    private static Dictionary<Guid, TaskTimeFacts> Index(params TaskTimeFacts[] tasks) => tasks.ToDictionary(t => t.Id);

    /// <summary>RPT-001: logged is the person's time in the range on every task, each task counted once per person.</summary>
    [Fact]
    public void Logged_is_all_the_persons_time_and_tasks_count_once()
    {
        var estimated = Task(240, 300);
        var unestimated = Task(null, 90);
        var cancelled = Task(60, 45, TaskItemStatus.Cancelled);
        var report = TimeReportRules.TimeByPerson(
            [
                new LoggedTime(Alice, estimated.Id, 120),
                new LoggedTime(Alice, estimated.Id, 30), // a duplicate group merges
                new LoggedTime(Alice, unestimated.Id, 60),
                new LoggedTime(Alice, cancelled.Id, 45)
            ],
            Index(estimated, unestimated, cancelled), Name);

        var alice = Assert.Single(report.Rows);
        Assert.Equal("Alice", alice.Name);
        Assert.Equal(255, alice.LoggedMinutes);
        Assert.Equal(3, alice.TaskCount);
        Assert.Equal(3, alice.Tasks.Count);
        Assert.Equal(150, alice.Tasks.Single(t => t.TaskId == estimated.Id).LoggedMinutes);
        Assert.Equal(255, report.TotalLoggedMinutes);
    }

    /// <summary>RPT-002: estimated against actual covers estimated, non-cancelled tasks, using each task's total to date.</summary>
    [Fact]
    public void Estimate_comparison_uses_estimated_open_and_done_tasks_against_their_totals()
    {
        var over = Task(240, 300);
        var under = Task(120, 60, TaskItemStatus.Done);
        var unestimated = Task(null, 90);
        var cancelled = Task(60, 500, TaskItemStatus.Cancelled);
        var report = TimeReportRules.TimeByPerson(
            [
                new LoggedTime(Alice, over.Id, 30),
                new LoggedTime(Alice, under.Id, 15),
                new LoggedTime(Alice, unestimated.Id, 90),
                new LoggedTime(Alice, cancelled.Id, 20)
            ],
            Index(over, under, unestimated, cancelled), Name);

        var alice = Assert.Single(report.Rows);
        Assert.Equal(1, alice.UnestimatedTasks);
        Assert.Equal(new EstimateComparison(2, 360, 360, 1), alice.Estimate);
        Assert.Equal(0, alice.Estimate.VarianceMinutes);

        var lines = alice.Tasks.ToDictionary(t => t.TaskId);
        Assert.True(lines[over.Id].IsOver);
        Assert.False(lines[under.Id].IsOver);
        Assert.False(lines[unestimated.Id].IsOver);
        Assert.True(lines[cancelled.Id].IsCancelled);
        Assert.False(lines[cancelled.Id].IsOver); // a cancelled task is never flagged, whatever was logged
        Assert.Equal(300, lines[over.Id].TotalMinutes);
    }

    /// <summary>RPT-003: a task two people worked on shows under both, but the total counts it once.</summary>
    [Fact]
    public void Shared_task_shows_under_each_person_and_counts_once_in_the_total()
    {
        var shared = Task(600, 540);
        var alicesOwn = Task(60, 90);
        var report = TimeReportRules.TimeByPerson(
            [
                new LoggedTime(Alice, shared.Id, 300),
                new LoggedTime(Bob, shared.Id, 240),
                new LoggedTime(Alice, alicesOwn.Id, 90)
            ],
            Index(shared, alicesOwn), Name);

        Assert.Equal(2, report.Rows.Count);
        var alice = report.Rows.Single(r => r.UserId == Alice);
        var bob = report.Rows.Single(r => r.UserId == Bob);
        Assert.Equal(new EstimateComparison(2, 660, 630, 1), alice.Estimate);
        Assert.Equal(new EstimateComparison(1, 600, 540, 0), bob.Estimate);

        Assert.Equal(630, report.TotalLoggedMinutes);
        Assert.Equal(2, report.TotalTasks);
        Assert.Equal(new EstimateComparison(2, 660, 630, 1), report.Total); // not 1260 estimated / 1170 actual
    }

    /// <summary>RPT-004: estimate accuracy groups completed tasks by assignee (none = Unassigned) with variance and over-count.</summary>
    [Fact]
    public void Estimate_accuracy_groups_by_assignee()
    {
        var report = TimeReportRules.EstimateAccuracy(
            [
                Task(240, 300, TaskItemStatus.Done, Alice),
                Task(120, 60, TaskItemStatus.Done, Alice),
                Task(null, 45, TaskItemStatus.Done, Alice),
                Task(60, 90, TaskItemStatus.Done, Bob),
                Task(30, 0, TaskItemStatus.Done)
            ],
            Name);

        Assert.Equal(5, report.DoneCount);
        Assert.Equal(["Alice", "Bob", "Unassigned"], report.Rows.Select(r => r.Name));

        var alice = report.Rows[0];
        Assert.Equal(3, alice.DoneCount);
        Assert.Equal(new EstimateComparison(2, 360, 360, 1), alice.Estimate);
        Assert.Equal(0, alice.Estimate.VariancePercent);

        var bob = report.Rows[1];
        Assert.Equal(30, bob.Estimate.VarianceMinutes);
        Assert.Equal(50, bob.Estimate.VariancePercent);

        var unassigned = report.Rows[2];
        Assert.Null(unassigned.UserId);
        Assert.Equal(-30, unassigned.Estimate.VarianceMinutes);
        Assert.Equal(-100, unassigned.Estimate.VariancePercent);

        Assert.Equal(new EstimateComparison(4, 450, 450, 2), report.Overall);
    }

    /// <summary>RPT-005: with nothing estimated there is no percentage and no variance.</summary>
    [Fact]
    public void Nothing_estimated_gives_no_percentage()
    {
        var t = Task(null, 120);
        var report = TimeReportRules.TimeByPerson([new LoggedTime(Alice, t.Id, 120)], Index(t), Name);
        var alice = Assert.Single(report.Rows);
        Assert.Equal(EstimateComparison.None, alice.Estimate);
        Assert.Null(alice.Estimate.VariancePercent);
        Assert.Equal(0, alice.Estimate.VarianceMinutes);
        Assert.Equal(1, report.TotalUnestimatedTasks);

        var accuracy = TimeReportRules.EstimateAccuracy([], Name);
        Assert.Empty(accuracy.Rows);
        Assert.Null(accuracy.Overall.VariancePercent);
    }

    /// <summary>RPT-006: people by time logged then name; their tasks by their time; accuracy rows by count, overruns first.</summary>
    [Fact]
    public void Ordering()
    {
        var carol = Guid.NewGuid();
        string WithCarol(Guid? id) => id == carol ? "carol" : Name(id);
        var a = Task(60, 60);
        var b = Task(60, 60);
        var c = Task(60, 60);
        var report = TimeReportRules.TimeByPerson(
            [
                new LoggedTime(carol, a.Id, 60),
                new LoggedTime(Bob, a.Id, 60),
                new LoggedTime(Alice, a.Id, 10),
                new LoggedTime(Alice, b.Id, 50),
                new LoggedTime(Alice, c.Id, 30)
            ],
            Index(a, b, c), WithCarol);
        Assert.Equal(["Alice", "Bob", "carol"], report.Rows.Select(r => r.Name)); // 90, then a 60/60 tie by name, ignoring case
        Assert.Equal([b.Id, c.Id, a.Id], report.Rows[0].Tasks.Select(t => t.TaskId));

        var bigOverrun = Task(60, 180, TaskItemStatus.Done, Bob);
        var smallOverrun = Task(60, 70, TaskItemStatus.Done, Bob);
        var under = Task(60, 30, TaskItemStatus.Done, Bob);
        var none = Task(null, 500, TaskItemStatus.Done, Bob);
        var accuracy = TimeReportRules.EstimateAccuracy(
            [none, under, Task(60, 60, TaskItemStatus.Done, Alice), smallOverrun, bigOverrun], Name);
        Assert.Equal(["Bob", "Alice"], accuracy.Rows.Select(r => r.Name));
        Assert.Equal([bigOverrun.Id, smallOverrun.Id, under.Id, none.Id], accuracy.Rows[0].Tasks.Select(t => t.TaskId));
    }

    /// <summary>RPT-007: a variance reads "+5h 30m", "-4h" or "0m".</summary>
    [Fact]
    public void Signed_variance_format()
    {
        Assert.Equal("+5h 30m", TimeFormat.Signed(330));
        Assert.Equal("-4h", TimeFormat.Signed(-240));
        Assert.Equal("-45m", TimeFormat.Signed(-45));
        Assert.Equal("0m", TimeFormat.Signed(0));
    }
}

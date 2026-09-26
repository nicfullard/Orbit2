using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Project status (spec §12).</summary>
public class ProjectReportRulesTests
{
    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 26);

    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();
    private static readonly Guid Dave = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> Names =
        new Dictionary<Guid, string> { [Alice] = "Alice", [Bob] = "Bob", [Carol] = "Carol", [Dave] = "Dave" };

    private static string Name(Guid? id) => id is Guid g && Names.TryGetValue(g, out var n) ? n : "Unassigned";

    private static int _number;

    private static DateTime Sep(int day) => new(2026, 9, day, 10, 0, 0, DateTimeKind.Utc);

    private static ProjectFacts Project(
        string name, ProjectStatus status = ProjectStatus.Active, DateTime? created = null, string department = "Ops", DateOnly? target = null) =>
        new(Guid.NewGuid(), $"P-26-{Interlocked.Increment(ref _number):00000}", name, status, department, "Owner",
            created ?? From.AddDays(-100), target);

    private static ProjectTaskFacts Task(
        ProjectFacts p, TaskItemStatus status = TaskItemStatus.InProgress, Guid? assignee = null, int? estimate = null,
        int total = 0, int period = 0, DateOnly? due = null, DateTime? created = null, DateTime? completed = null) =>
        new(new TaskTimeFacts(Guid.NewGuid(), $"T-26-{Interlocked.Increment(ref _number):00000}", "Task", status, assignee, estimate, total),
            p.Id, due, created ?? From.AddDays(-50), completed, period);

    private static ProjectStatusChange Change(ProjectFacts p, DateTime at, ProjectStatus from, ProjectStatus to) => new(p.Id, at, from, to);

    private static ProjectStatusReport Build(
        ProjectFacts[] projects, ProjectTaskFacts[]? tasks = null, ProjectPersonTime[]? time = null,
        ProjectStatusChange[]? changes = null, Dictionary<Guid, CriticalPathSummary>? schedules = null)
    {
        var histories = projects.ToDictionary(p => p.Id,
            p => ProjectReportRules.History(p.Status, (changes ?? []).Where(c => c.ProjectId == p.Id), From, To));
        return ProjectReportRules.Build(projects, histories, tasks ?? [], time ?? [],
            schedules ?? new Dictionary<Guid, CriticalPathSummary>(), Name, From, To, Today);
    }

    private static CriticalPathSummary Analysis(BufferStatus status, bool stale = false) =>
        new(Guid.NewGuid(), Sep(20), stale, new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 1), status, 3, 40, 2, 1, 0);

    /// <summary>RPT-009: the status at the start and end of the period, and the changes in it, come from the changes since its start.</summary>
    [Fact]
    public void Status_over_the_period_is_rebuilt_from_the_changes_since_its_start()
    {
        var closedInPeriod = Project("Closed", ProjectStatus.Completed);
        var h = ProjectReportRules.History(closedInPeriod.Status,
            [Change(closedInPeriod, Sep(12), ProjectStatus.Active, ProjectStatus.Completed)], From, To);
        Assert.Equal(ProjectStatus.Active, h.AtStart);
        Assert.Equal(ProjectStatus.Completed, h.AtEnd);
        Assert.Equal(ProjectStatus.Completed, h.Current);
        Assert.Equal(ProjectStatus.Completed, Assert.Single(h.Changes).To);
        Assert.True(h.WasOpen);

        // Changed in the period and again after it: the end of the period shows the first, "now" the second.
        var archivedLater = Project("Archived later", ProjectStatus.Archived);
        h = ProjectReportRules.History(archivedLater.Status,
            [
                Change(archivedLater, To.AddDays(4), ProjectStatus.OnHold, ProjectStatus.Archived),
                Change(archivedLater, Sep(12), ProjectStatus.Active, ProjectStatus.OnHold)
            ], From, To);
        Assert.Equal(ProjectStatus.Active, h.AtStart);
        Assert.Equal(ProjectStatus.OnHold, h.AtEnd);
        Assert.Equal(ProjectStatus.Archived, h.Current);
        Assert.Equal(ProjectStatus.OnHold, Assert.Single(h.Changes).To);

        // Nothing recorded: the current status throughout.
        h = ProjectReportRules.History(ProjectStatus.OnHold, [], From, To);
        Assert.Equal((ProjectStatus.OnHold, ProjectStatus.OnHold), (h.AtStart, h.AtEnd));
        Assert.Empty(h.Changes);
        Assert.True(h.WasOpen);
    }

    /// <summary>RPT-009: status changes are read from the audit details the project service writes, and nothing else.</summary>
    [Fact]
    public void Reads_the_status_change_the_audit_trail_records()
    {
        var update = JsonSerializer.Serialize(new ChangeSet()
            .TrackText("name", "Old", "New")
            .Track("status", ProjectStatus.Active, ProjectStatus.Completed).Changes, OrbitJson.Options);
        Assert.True(ProjectReportRules.TryReadStatusChange(update, out var from, out var to));
        Assert.Equal((ProjectStatus.Active, ProjectStatus.Completed), (from, to));

        var archive = JsonSerializer.Serialize(new { status = new { from = ProjectStatus.OnHold, to = ProjectStatus.Archived } }, OrbitJson.Options);
        Assert.True(ProjectReportRules.TryReadStatusChange(archive, out from, out to));
        Assert.Equal((ProjectStatus.OnHold, ProjectStatus.Archived), (from, to));

        var nameOnly = JsonSerializer.Serialize(new ChangeSet().TrackText("name", "Old", "New").Changes, OrbitJson.Options);
        Assert.False(ProjectReportRules.TryReadStatusChange(nameOnly, out _, out _));
        Assert.False(ProjectReportRules.TryReadStatusChange("""{"number":"P-26-00001","status":"Active"}""", out _, out _)); // Created
        Assert.False(ProjectReportRules.TryReadStatusChange("""{"status":{"from":"Active","to":"Paused"}}""", out _, out _));
        Assert.False(ProjectReportRules.TryReadStatusChange("not json", out _, out _));
        Assert.False(ProjectReportRules.TryReadStatusChange(null, out _, out _));
    }

    /// <summary>RPT-010: a project closed before the period is in it only when there was activity; a new one starts as created.</summary>
    [Fact]
    public void A_project_closed_before_the_period_is_in_it_only_with_activity()
    {
        var old = Project("Old", ProjectStatus.Completed);
        var before = ProjectReportRules.History(old.Status, [], From, To); // its closing change predates the period
        Assert.False(before.WasOpen);
        Assert.False(ProjectReportRules.Includes(before, hadActivity: false));
        Assert.True(ProjectReportRules.Includes(before, hadActivity: true));

        // Reopened during the period.
        var reopened = ProjectReportRules.History(ProjectStatus.Active,
            [Change(old, Sep(20), ProjectStatus.Completed, ProjectStatus.Active)], From, To);
        Assert.Equal(ProjectStatus.Completed, reopened.AtStart);
        Assert.True(ProjectReportRules.Includes(reopened, hadActivity: false));

        // Created in the period as Active and completed in it.
        var created = Project("New", ProjectStatus.Completed, created: Sep(3));
        var history = ProjectReportRules.History(created.Status, [Change(created, Sep(20), ProjectStatus.Active, ProjectStatus.Completed)], From, To);
        Assert.Equal(ProjectStatus.Active, history.AtStart);
        Assert.True(ProjectReportRules.Includes(history, hadActivity: false));
        Assert.True(Build([created], changes: [Change(created, Sep(20), ProjectStatus.Active, ProjectStatus.Completed)]).Rows[0].CreatedInPeriod);
    }

    /// <summary>RPT-011: open, overdue and blocked are now; created and done count the period; cancelled tasks aren't estimated.</summary>
    [Fact]
    public void Task_counts_are_now_apart_from_created_and_done_in_the_period()
    {
        var p = Project("Build", target: Today.AddDays(-1));
        var report = Build([p],
        [
            Task(p, TaskItemStatus.Todo, due: Today.AddDays(-1), created: Sep(2)),               // overdue, new
            Task(p, TaskItemStatus.Blocked),
            Task(p, TaskItemStatus.InProgress, due: Today, total: 20, period: 20),              // due today isn't overdue
            Task(p, TaskItemStatus.Done, estimate: 60, total: 90, period: 30, completed: Sep(10)), // over
            Task(p, TaskItemStatus.Done, estimate: 120, total: 100, completed: From.AddDays(-3)),
            Task(p, TaskItemStatus.Cancelled, estimate: 30, total: 50, due: Today.AddDays(-9))  // closed: never overdue
        ]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(new ProjectTaskCounts(6, 3, 1, 1, 2, 1, 1, 1), row.Tasks);
        Assert.Equal(33, row.Tasks.PercentDone);
        Assert.Equal(new EstimateComparison(2, 180, 190, 1), row.Estimate);
        Assert.Equal(50, row.LoggedInPeriodMinutes);
        Assert.Equal(260, row.LoggedToDateMinutes);
        Assert.True(row.IsPastTarget);

        // A closed project is never past its target.
        var done = Project("Done", ProjectStatus.Completed, target: Today.AddDays(-1));
        Assert.False(Build([done], changes: [Change(done, Sep(5), ProjectStatus.Active, ProjectStatus.Completed)]).Rows[0].IsPastTarget);
    }

    /// <summary>RPT-012: a row per person with time or a task, estimates of their live tasks, Unassigned last, adding up to the project.</summary>
    [Fact]
    public void People_add_up_to_the_project()
    {
        var p = Project("Build");
        var bobs = Task(p, TaskItemStatus.Todo, Bob, estimate: 120, total: 10, period: 10);
        var report = Build([p],
            [
                Task(p, TaskItemStatus.Done, Alice, estimate: 60, total: 70, period: 30, completed: Sep(8)),
                Task(p, TaskItemStatus.InProgress, Alice, total: 30, period: 10),
                bobs,
                Task(p, TaskItemStatus.Blocked, estimate: 30),              // unassigned
                Task(p, TaskItemStatus.Cancelled, Dave, estimate: 500)      // Dave has only a cancelled task and no time
            ],
            [
                new ProjectPersonTime(p.Id, Alice, 40, 100),
                new ProjectPersonTime(p.Id, Carol, 10, 10)                  // Carol logged on Bob's task
            ]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(["Alice", "Carol", "Bob", "Unassigned"], row.People.Select(x => x.Name));
        Assert.Equal(new ProjectPersonRow(Alice, "Alice", 1, 1, 1, 60, 40, 100), row.People[0]);
        Assert.Equal(new ProjectPersonRow(Carol, "Carol", 0, 0, 0, 0, 10, 10), row.People[1]);
        Assert.Equal(new ProjectPersonRow(Bob, "Bob", 1, 0, 1, 120, 0, 0), row.People[2]);
        Assert.Equal(new ProjectPersonRow(null, "Unassigned", 1, 0, 1, 30, 0, 0), row.People[3]);

        Assert.Equal(row.Tasks.Open, row.People.Sum(x => x.OpenTasks));
        Assert.Equal(row.Tasks.DoneInPeriod, row.People.Sum(x => x.DoneInPeriod));
        Assert.Equal(row.Estimate.EstimatedMinutes, row.People.Sum(x => x.EstimatedMinutes));
        Assert.Equal(row.LoggedInPeriodMinutes, row.People.Sum(x => x.LoggedInPeriodMinutes));
        Assert.Equal(row.LoggedToDateMinutes, row.People.Sum(x => x.LoggedToDateMinutes));
    }

    /// <summary>RPT-013: the task list is what's open plus what was worked on in the period, by status, due date and number.</summary>
    [Fact]
    public void Task_list_is_open_work_and_work_in_the_period()
    {
        var p = Project("Build");
        var doneLongAgo = Task(p, TaskItemStatus.Done, completed: From.AddDays(-30));
        var cancelledLongAgo = Task(p, TaskItemStatus.Cancelled);
        var doneInPeriod = Task(p, TaskItemStatus.Done, completed: Sep(4));
        var doneBeforeButLogged = Task(p, TaskItemStatus.Done, total: 90, period: 15, completed: From.AddDays(-2));
        var cancelledNew = Task(p, TaskItemStatus.Cancelled, created: Sep(6));
        var todoNoDue = Task(p, TaskItemStatus.Todo);
        var todoDue = Task(p, TaskItemStatus.Todo, due: new DateOnly(2026, 10, 3));
        var todoOverdue = Task(p, TaskItemStatus.Todo, Alice, due: new DateOnly(2026, 9, 20));
        var blocked = Task(p, TaskItemStatus.Blocked, due: new DateOnly(2026, 9, 1));
        var inProgress = Task(p, TaskItemStatus.InProgress);

        var row = Build([p],
            [doneLongAgo, cancelledLongAgo, doneInPeriod, doneBeforeButLogged, cancelledNew, todoNoDue, todoDue, todoOverdue, blocked, inProgress])
            .Rows.Single();

        Assert.Equal(
            new[] { todoOverdue, todoDue, todoNoDue, inProgress, blocked, doneInPeriod, doneBeforeButLogged, cancelledNew }.Select(t => t.Task.Id),
            row.TaskRows.Select(t => t.TaskId));
        var first = row.TaskRows[0];
        Assert.True(first.IsOverdue);
        Assert.Equal("Alice", first.Assignee);
        Assert.Equal("Unassigned", row.TaskRows[1].Assignee);
        Assert.True(row.TaskRows.Single(t => t.TaskId == doneInPeriod.Task.Id).DoneInPeriod);
        Assert.True(row.TaskRows.Single(t => t.TaskId == cancelledNew.Task.Id).CreatedInPeriod);
    }

    /// <summary>RPT-014: rows by department then name, the totals over every project, and the count of each status at the end.</summary>
    [Fact]
    public void Totals_and_status_counts_cover_every_project()
    {
        var steady = Project("Steady", department: "Ops");
        var finished = Project("Finished", ProjectStatus.Completed, department: "Ops");
        var laterArchived = Project("Archived later", ProjectStatus.Archived, department: "Finance");
        var fresh = Project("Fresh", created: Sep(10), department: "Finance");
        var report = Build([steady, finished, laterArchived, fresh],
            [
                Task(steady, TaskItemStatus.Todo, estimate: 60, total: 30, period: 30),
                Task(finished, TaskItemStatus.Done, estimate: 60, total: 90, period: 45, completed: Sep(11)),
                Task(fresh, TaskItemStatus.Todo, created: Sep(10))
            ],
            [new ProjectPersonTime(steady.Id, Alice, 30, 30), new ProjectPersonTime(finished.Id, Alice, 45, 90)],
            [
                Change(finished, Sep(12), ProjectStatus.Active, ProjectStatus.Completed),
                Change(laterArchived, Sep(15), ProjectStatus.Active, ProjectStatus.Completed),
                Change(laterArchived, To.AddDays(3), ProjectStatus.Completed, ProjectStatus.Archived)
            ],
            new Dictionary<Guid, CriticalPathSummary>
            {
                [steady.Id] = Analysis(BufferStatus.Amber, stale: true),
                [finished.Id] = Analysis(BufferStatus.Red) // closed now: not shown
            });

        Assert.Equal(["Archived later", "Fresh", "Finished", "Steady"], report.Rows.Select(r => r.Project.Name));
        Assert.Equal(4, report.Total.Projects);
        Assert.Equal(new ProjectTaskCounts(3, 2, 0, 0, 1, 0, 1, 1), report.Total.Tasks);
        Assert.Equal(75, report.Total.LoggedInPeriodMinutes);
        Assert.Equal(120, report.Total.LoggedToDateMinutes);
        Assert.Equal(new EstimateComparison(2, 120, 120, 1), report.Total.Estimate);

        Assert.Equal(
            [new ProjectStatusCount(ProjectStatus.Active, 2, 0), new ProjectStatusCount(ProjectStatus.Completed, 2, 2)],
            report.StatusCounts);

        Assert.Equal(BufferStatus.Amber, report.Rows.Single(r => r.Project.Id == steady.Id).Schedule?.BufferStatus);
        Assert.Null(report.Rows.Single(r => r.Project.Id == finished.Id).Schedule);
        Assert.Empty(report.Rows.Single(r => r.Project.Id == laterArchived.Id).People);
    }
}

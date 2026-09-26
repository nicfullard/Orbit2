using System.Text.Json;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Project status report (§12): turns what <c>ReportingService</c> reads in SQL into report rows.
/// <list type="bullet">
/// <item>A project's status over the range is rebuilt backwards from its status now and the changes its audit trail
/// records from the start of the range on.</item>
/// <item>Task counts are as they stand now, apart from the tasks created and completed in the range; time is both in
/// the range and to date.</item>
/// <item>Estimates are compared over tasks that have one and aren't Cancelled, against all time logged on them to date,
/// as in the time reports (<see cref="TimeReportRules.Compare"/>).</item>
/// </list>
/// </summary>
public static class ProjectReportRules
{
    /// <summary>
    /// Reads a project audit row's <c>Details</c> for a status change: <c>{"status":{"from":"Active","to":"Completed"}}</c>,
    /// as <see cref="ChangeSet"/> and <c>ProjectService.ArchiveAsync</c> write it. False for any other change.
    /// </summary>
    public static bool TryReadStatusChange(string? details, out ProjectStatus from, out ProjectStatus to)
    {
        from = to = default;
        if (string.IsNullOrWhiteSpace(details)) return false;
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
                && status.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String
                && Enum.TryParse(f.GetString(), out from)
                && Enum.TryParse(t.GetString(), out to);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The project's status at the start and end of [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) and the
    /// changes within it. The status at any moment is the <c>From</c> of the first change after it, or the current status
    /// when nothing has changed since, so only changes from <paramref name="fromUtc"/> on are needed. A project with no
    /// recorded change has had its current status throughout; one created in the range starts with the status it was
    /// created with.
    /// </summary>
    public static ProjectStatusHistory History(
        ProjectStatus current, IEnumerable<ProjectStatusChange> changes, DateTime fromUtc, DateTime toUtc)
    {
        var since = changes.Where(c => c.At >= fromUtc).OrderBy(c => c.At).ToList();
        var atStart = since.Count > 0 ? since[0].From : current;
        var atEnd = since.FirstOrDefault(c => c.At >= toUtc)?.From ?? current;
        return new ProjectStatusHistory(atStart, atEnd, current, since.Where(c => c.At < toUtc).ToList());
    }

    /// <summary>A project is in the range when it was open at some point in it, or had task or time activity in it.</summary>
    public static bool Includes(ProjectStatusHistory history, bool hadActivity) => history.WasOpen || hadActivity;

    /// <summary>
    /// The report over the projects already chosen (<see cref="Includes"/>): one row per project ordered by department
    /// then name, with its people and its task list, and the totals.
    /// </summary>
    public static ProjectStatusReport Build(
        IEnumerable<ProjectFacts> projects,
        IReadOnlyDictionary<Guid, ProjectStatusHistory> histories,
        IEnumerable<ProjectTaskFacts> tasks,
        IEnumerable<ProjectPersonTime> time,
        IReadOnlyDictionary<Guid, CriticalPathSummary> schedules,
        Func<Guid?, string> name,
        DateTime fromUtc, DateTime toUtc, DateOnly today)
    {
        var tasksByProject = tasks.DistinctBy(t => t.Task.Id).ToLookup(t => t.ProjectId);
        var timeByProject = time.ToLookup(t => t.ProjectId);
        bool InRange(DateTime? at) => at >= fromUtc && at < toUtc;

        var rows = projects.DistinctBy(p => p.Id).Select(p =>
            {
                var history = histories.TryGetValue(p.Id, out var h) ? h : History(p.Status, [], fromUtc, toUtc);
                var projectTasks = tasksByProject[p.Id].ToList();
                var open = history.Current.IsOpen();
                return new ProjectStatusRow(
                    p, history, InRange(p.CreatedAt), Count(projectTasks, fromUtc, toUtc, today),
                    projectTasks.Sum(t => t.PeriodMinutes), projectTasks.Sum(t => t.Task.TotalMinutes),
                    TimeReportRules.Compare(projectTasks.Select(t => t.Task)),
                    open && p.TargetDate < today,
                    open && schedules.TryGetValue(p.Id, out var s) ? s : null,
                    People(projectTasks, timeByProject[p.Id], name, fromUtc, toUtc),
                    TaskList(projectTasks, name, fromUtc, toUtc, today));
            })
            .OrderBy(r => r.Project.DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Project.Number, StringComparer.Ordinal)
            .ToList();

        var total = new ProjectStatusTotals(
            rows.Count,
            rows.Aggregate(ProjectTaskCounts.None, (sum, r) => sum.Plus(r.Tasks)),
            rows.Sum(r => r.LoggedInPeriodMinutes),
            rows.Sum(r => r.LoggedToDateMinutes),
            TimeReportRules.Compare(rows.SelectMany(r => tasksByProject[r.Project.Id]).Select(t => t.Task)));

        var counts = rows.GroupBy(r => r.Status.AtEnd)
            .OrderBy(g => g.Key)
            .Select(g => new ProjectStatusCount(g.Key, g.Count(), g.Count(r => r.Status.AtStart != g.Key)))
            .ToList();

        return new ProjectStatusReport(rows, total, counts);
    }

    private static ProjectTaskCounts Count(IReadOnlyList<ProjectTaskFacts> tasks, DateTime fromUtc, DateTime toUtc, DateOnly today) => new(
        tasks.Count,
        tasks.Count(t => !t.Task.Status.IsClosed()),
        tasks.Count(t => t.Task.Status == TaskItemStatus.Blocked),
        tasks.Count(t => IsOverdue(t, today)),
        tasks.Count(t => t.Task.Status == TaskItemStatus.Done),
        tasks.Count(t => t.Task.Status == TaskItemStatus.Cancelled),
        tasks.Count(t => t.CreatedAt >= fromUtc && t.CreatedAt < toUtc),
        tasks.Count(t => IsDoneIn(t, fromUtc, toUtc)));

    /// <summary>
    /// One row per person with time logged on the project or a task on it that isn't Cancelled, and an Unassigned row for
    /// such tasks with nobody on them, so the rows add up to the project's figures. Ordered by time in the range, then time
    /// to date, then name; Unassigned last.
    /// </summary>
    private static IReadOnlyList<ProjectPersonRow> People(
        IReadOnlyList<ProjectTaskFacts> tasks, IEnumerable<ProjectPersonTime> time, Func<Guid?, string> name,
        DateTime fromUtc, DateTime toUtc)
    {
        var logged = time.GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => (Period: g.Sum(t => t.PeriodMinutes), Total: g.Sum(t => t.TotalMinutes)));
        var assigned = tasks.Where(t => t.Task.Status != TaskItemStatus.Cancelled).ToLookup(t => t.Task.AssigneeId);

        var people = logged.Keys.Select(id => (Guid?)id)
            .Union(assigned.Select(g => g.Key).Where(id => id is not null))
            .Select(id =>
            {
                var mine = assigned[id].ToList();
                var estimated = mine.Where(t => t.Task.EstimateMinutes is not null).ToList();
                var (period, total) = logged.TryGetValue(id!.Value, out var l) ? l : (0, 0);
                return new ProjectPersonRow(id, name(id), mine.Count(t => !t.Task.Status.IsClosed()),
                    mine.Count(t => IsDoneIn(t, fromUtc, toUtc)), estimated.Count,
                    estimated.Sum(t => t.Task.EstimateMinutes!.Value), period, total);
            })
            .OrderByDescending(r => r.LoggedInPeriodMinutes)
            .ThenByDescending(r => r.LoggedToDateMinutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var unassigned = assigned[null].ToList();
        if (unassigned.Count > 0)
        {
            var estimated = unassigned.Where(t => t.Task.EstimateMinutes is not null).ToList();
            people.Add(new ProjectPersonRow(null, name(null), unassigned.Count(t => !t.Task.Status.IsClosed()),
                unassigned.Count(t => IsDoneIn(t, fromUtc, toUtc)), estimated.Count,
                estimated.Sum(t => t.Task.EstimateMinutes!.Value), 0, 0));
        }
        return people;
    }

    /// <summary>
    /// The tasks open now, and the closed ones created, completed or logged against in the range; closed tasks untouched
    /// in the range are left out. Ordered by status in its declared order, then due date (none last), then number.
    /// </summary>
    private static IReadOnlyList<ProjectTaskRow> TaskList(
        IReadOnlyList<ProjectTaskFacts> tasks, Func<Guid?, string> name, DateTime fromUtc, DateTime toUtc, DateOnly today) =>
        tasks.Select(t =>
            {
                var created = t.CreatedAt >= fromUtc && t.CreatedAt < toUtc;
                var done = IsDoneIn(t, fromUtc, toUtc);
                return (Facts: t, Created: created, Done: done,
                    Listed: !t.Task.Status.IsClosed() || created || done || t.PeriodMinutes > 0);
            })
            .Where(x => x.Listed)
            .Select(x => new ProjectTaskRow(x.Facts.Task.Id, x.Facts.Task.Number, x.Facts.Task.Title, x.Facts.Task.Status,
                name(x.Facts.Task.AssigneeId), x.Facts.DueDate, IsOverdue(x.Facts, today), x.Facts.Task.EstimateMinutes,
                x.Facts.PeriodMinutes, x.Facts.Task.TotalMinutes, x.Created, x.Done))
            .OrderBy(r => r.Status)
            .ThenBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();

    private static bool IsDoneIn(ProjectTaskFacts t, DateTime fromUtc, DateTime toUtc) =>
        t.Task.Status == TaskItemStatus.Done && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc;

    private static bool IsOverdue(ProjectTaskFacts t, DateOnly today) =>
        !t.Task.Status.IsClosed() && t.DueDate is DateOnly due && due < today;
}

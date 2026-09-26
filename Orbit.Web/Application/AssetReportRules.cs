using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Asset status report (§12): turns what <c>ReportingService</c> reads in SQL into report rows.
/// <list type="bullet">
/// <item>An asset's status over the range is rebuilt backwards from its status now and the status changes its audit trail
/// records from the start of the range on, as for projects - except that a disposal counts from its disposal date.</item>
/// <item>Type, location and managing department are as they are now: the audit trail records them by name only.</item>
/// <item>Checks are read as at the range's last day, or today when that is sooner (<see cref="AsAt"/>). Open linked tasks are as
/// things stand now; tasks created and done, and time logged, count the range.</item>
/// </list>
/// </summary>
public static class AssetReportRules
{
    public const string NoLocation = "No location";

    /// <summary>
    /// Reads an asset audit row's <c>Details</c> for a status change, as <c>AssetService</c> writes it:
    /// <c>{"status":{"from":"Active","to":"Disposed"},"disposedOn":{…}}</c>. False for any other row, and for one that changes only
    /// the disposal date (the same status on both sides).
    /// </summary>
    public static bool TryReadStatusChange(string? details, out AssetStatus from, out AssetStatus to) =>
        AuditStatusChange.TryRead(details, out from, out to) && from != to;

    /// <summary>The day checks are read as at: the range's last day, or today when the range runs on past it.</summary>
    public static DateOnly AsAt(DateTime toUtc, DateOnly today)
    {
        var lastDay = DateOnly.FromDateTime(toUtc).AddDays(-1);
        return lastDay < today ? lastDay : today;
    }

    /// <summary>
    /// The asset's status at the start and end of [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) and the changes within it,
    /// from the changes recorded from <paramref name="fromUtc"/> on - as <see cref="ProjectReportRules.History"/> does. A disposal
    /// counts from its disposal date: when the asset is still Disposed, the change that disposed of it takes effect at the start of
    /// <paramref name="disposedOn"/> if that is earlier than when it was recorded, but never before the change recorded ahead of
    /// it. An asset with no recorded change has had its current status throughout; one registered in the range starts with the
    /// status it was registered with.
    /// </summary>
    public static AssetStatusHistory History(
        AssetStatus current, DateOnly? disposedOn, IEnumerable<AssetStatusChange> changes, DateTime fromUtc, DateTime toUtc)
    {
        var ordered = changes.OrderBy(c => c.At).ToList();
        if (current == AssetStatus.Disposed && disposedOn is DateOnly day && ordered.Count > 0 && ordered[^1].To == AssetStatus.Disposed)
        {
            var effective = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            if (ordered.Count > 1 && effective < ordered[^2].At) effective = ordered[^2].At;
            if (effective < ordered[^1].At) ordered[^1] = ordered[^1] with { At = effective };
        }

        var since = ordered.Where(c => c.At >= fromUtc).ToList();
        var atStart = since.Count > 0 ? since[0].From : current;
        var atEnd = since.FirstOrDefault(c => c.At >= toUtc)?.From ?? current;
        return new AssetStatusHistory(atStart, atEnd, current, since.Where(c => c.At < toUtc).ToList());
    }

    /// <summary>
    /// An asset registered before the range ends is in it when it wasn't Disposed at some point in it, when it was registered in it,
    /// or when a linked task was created, completed or logged against in it (late work on a disposed asset).
    /// </summary>
    public static bool Includes(AssetStatusHistory history, bool registeredInPeriod, bool hadTaskActivity) =>
        history.WasInService || registeredInPeriod || hadTaskActivity;

    /// <summary>
    /// The report over the assets already chosen (<see cref="Includes"/>): the figures by type (each with its locations) and by
    /// location (each with its types), ordered by department and then name; the totals; the status counts at the end of the range;
    /// and the assets with linked-task work, with those tasks.
    /// </summary>
    public static AssetStatusReport Build(
        IEnumerable<AssetFacts> assets,
        IReadOnlyDictionary<Guid, AssetStatusHistory> histories,
        IEnumerable<AssetTaskFacts> tasks,
        Func<Guid?, string> name,
        DateTime fromUtc, DateTime toUtc, DateOnly today)
    {
        var asAt = AsAt(toUtc, today);
        var tasksByAsset = tasks.DistinctBy(t => t.Id).ToLookup(t => t.AssetId);
        var lines = assets.DistinctBy(a => a.Id).Select(a =>
            {
                var history = histories.TryGetValue(a.Id, out var h) ? h : History(a.Status, a.DisposedOn, [], fromUtc, toUtc);
                var linked = tasksByAsset[a.Id].ToList();
                return new Line(a, history.AtStart, history.AtEnd, Count(a, history.AtEnd, linked, fromUtc, toUtc, asAt, today), linked);
            })
            .ToList();

        var byType = lines.GroupBy(l => l.Asset.TypeId)
            .Select(g => TypeRow(g.ToList(), LocationRows(g)))
            .OrderBy(r => r, TypeOrder)
            .ToList();
        var byLocation = LocationRows(lines, TypeRows);

        var counts = lines.GroupBy(l => l.AtEnd)
            .OrderBy(g => g.Key)
            .Select(g => new AssetStatusCount(g.Key, g.Count(), g.Count(l => l.AtStart != g.Key)))
            .ToList();

        var work = lines
            .Select(l => (Line: l, Tasks: TaskList(l.Tasks, name, fromUtc, toUtc, today)))
            .Where(x => x.Tasks.Count > 0)
            .Select(x => new AssetWorkRow(x.Line.Asset.Id, x.Line.Asset.AssetNumber, x.Line.Asset.Name, x.Line.Asset.TypeName,
                x.Line.Asset.LocationName, x.Line.Asset.DepartmentName, x.Line.AtEnd, x.Line.Counts.Tasks, x.Tasks))
            .OrderBy(r => r.DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.AssetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AssetStatusReport(byType, byLocation, Sum(lines), counts, work, asAt);
    }

    /// <summary>One asset, read once: its status at the start and end of the range, its figures and its linked tasks.</summary>
    private sealed record Line(AssetFacts Asset, AssetStatus AtStart, AssetStatus AtEnd, AssetGroupCounts Counts, IReadOnlyList<AssetTaskFacts> Tasks);

    private static AssetGroupCounts Count(
        AssetFacts a, AssetStatus atEnd, IReadOnlyList<AssetTaskFacts> tasks, DateTime fromUtc, DateTime toUtc, DateOnly asAt, DateOnly today)
    {
        var held = atEnd != AssetStatus.Disposed;
        var due = AssetCheckSchedule.NextDue(atEnd, a.CheckIntervalDays, a.LastCheckedOn, DateOnly.FromDateTime(a.CreatedAt));
        return new AssetGroupCounts(
            atEnd == AssetStatus.Active ? 1 : 0,
            atEnd == AssetStatus.InStorage ? 1 : 0,
            atEnd == AssetStatus.Damaged ? 1 : 0,
            atEnd == AssetStatus.Lost ? 1 : 0,
            held ? 0 : 1,
            a.CreatedAt >= fromUtc && a.CreatedAt < toUtc ? 1 : 0,
            held ? a.PurchaseValue ?? 0m : 0m,
            held && a.PurchaseValue is null ? 1 : 0,
            held ? 0m : a.PurchaseValue ?? 0m,
            held && a.ChecksInPeriod > 0 ? 1 : 0,
            AssetCheckSchedule.StateOf(due, asAt, 0) == CheckDueState.Overdue ? 1 : 0,
            held && a.LastCheckOutcome is AssetCheckOutcome outcome && outcome != AssetCheckOutcome.Ok ? 1 : 0,
            new AssetTaskCounts(
                tasks.Count(t => !t.Status.IsClosed()),
                tasks.Count(t => IsOverdue(t, today)),
                tasks.Count(t => t.CreatedAt >= fromUtc && t.CreatedAt < toUtc),
                tasks.Count(t => IsDoneIn(t, fromUtc, toUtc)),
                tasks.Sum(t => t.PeriodMinutes)));
    }

    private static AssetGroupCounts Sum(IEnumerable<Line> lines) => lines.Aggregate(AssetGroupCounts.None, (sum, l) => sum.Plus(l.Counts));

    private static AssetGroupRow TypeRow(IReadOnlyList<Line> lines, IReadOnlyList<AssetGroupRow> breakdown)
    {
        var a = lines[0].Asset;
        return new AssetGroupRow(a.TypeId, a.TypeName, a.TypeCategory, a.DepartmentName, Sum(lines), breakdown);
    }

    /// <summary>Types by department, then category (none first, as in the Assets list's filter), then name.</summary>
    private static IReadOnlyList<AssetGroupRow> TypeRows(IEnumerable<Line> lines) =>
        lines.GroupBy(l => l.Asset.TypeId).Select(g => TypeRow(g.ToList(), [])).OrderBy(r => r, TypeOrder).ToList();

    private static IReadOnlyList<AssetGroupRow> LocationRows(IEnumerable<Line> lines) => LocationRows(lines, _ => []);

    /// <summary>
    /// Locations by department, then name, with "No location" last in each department - a location belongs to one department, so
    /// the assets without one are grouped per department too.
    /// </summary>
    private static IReadOnlyList<AssetGroupRow> LocationRows(
        IEnumerable<Line> lines, Func<IReadOnlyList<Line>, IReadOnlyList<AssetGroupRow>> breakdown) =>
        lines.GroupBy(l => (l.Asset.DepartmentId, l.Asset.LocationId))
            .Select(g =>
            {
                var group = g.ToList();
                var a = group[0].Asset;
                return new AssetGroupRow(a.LocationId, a.LocationName ?? NoLocation, null, a.DepartmentName, Sum(group), breakdown(group));
            })
            .OrderBy(r => r.DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Id is null)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static readonly IComparer<AssetGroupRow> TypeOrder = Comparer<AssetGroupRow>.Create((x, y) =>
    {
        var c = StringComparer.CurrentCultureIgnoreCase.Compare(x.DepartmentName, y.DepartmentName);
        if (c == 0) c = StringComparer.CurrentCultureIgnoreCase.Compare(x.Category, y.Category);
        if (c == 0) c = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
        return c;
    });

    /// <summary>
    /// The asset's tasks that are open now, and the closed ones created, completed or logged against in the range, as in the project
    /// report. Ordered by status in its declared order, then due date (none last), then number.
    /// </summary>
    private static IReadOnlyList<AssetTaskRow> TaskList(
        IReadOnlyList<AssetTaskFacts> tasks, Func<Guid?, string> name, DateTime fromUtc, DateTime toUtc, DateOnly today) =>
        tasks.Select(t => (Task: t, Created: t.CreatedAt >= fromUtc && t.CreatedAt < toUtc, Done: IsDoneIn(t, fromUtc, toUtc)))
            .Where(x => !x.Task.Status.IsClosed() || x.Created || x.Done || x.Task.PeriodMinutes > 0)
            .Select(x => new AssetTaskRow(x.Task.Id, x.Task.Number, x.Task.Title, x.Task.Status, name(x.Task.AssigneeId),
                x.Task.DueDate, IsOverdue(x.Task, today), x.Task.CompletedAt, x.Task.PeriodMinutes, x.Created, x.Done))
            .OrderBy(r => r.Status)
            .ThenBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();

    private static bool IsDoneIn(AssetTaskFacts t, DateTime fromUtc, DateTime toUtc) =>
        t.Status == TaskItemStatus.Done && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc;

    private static bool IsOverdue(AssetTaskFacts t, DateOnly today) =>
        !t.Status.IsClosed() && t.DueDate is DateOnly due && due < today;
}

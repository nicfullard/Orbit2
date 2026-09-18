using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>
/// The Gantt view (spec §6.16) as geometry. Built once per request from a project's tasks and links: rows in tree
/// order, bars from planned start to due, milestones for due-only tasks, summary spans derived from subtasks, the day
/// axis, the dependency arrow paths and the critical path. Pure - nothing here is stored.
/// </summary>
public sealed class GanttChart
{
    public const int RowHeight = 34;
    public const int HeaderHeight = 44;
    public const int BarTop = 8;
    public const int BarHeight = 16;
    public const int SummaryTop = 27;
    public const int SummaryHeight = 4;
    public const int MilestoneSize = 14;
    private const int PaddingDays = 3;

    public required DateOnly From { get; init; }
    /// <summary>Inclusive.</summary>
    public required DateOnly To { get; init; }
    public required DateOnly Today { get; init; }
    public required int PxPerDay { get; init; }
    public required IReadOnlyList<GanttRow> Rows { get; init; }
    public required IReadOnlyList<GanttArrow> Arrows { get; init; }
    public required IReadOnlyList<GanttMonth> Months { get; init; }
    public required IReadOnlyList<GanttDay> Days { get; init; }
    /// <summary>Links between two listed tasks that can't be drawn because one end has no dates.</summary>
    public required IReadOnlyList<TaskDependency> UndrawnLinks { get; init; }
    /// <summary>Tasks on the critical path (zero total float).</summary>
    public required IReadOnlySet<Guid> CriticalIds { get; init; }
    /// <summary>The day the critical path ends on - the project's earliest possible finish given the links.</summary>
    public DateOnly? CriticalEnd { get; init; }

    public int DayCount => To.DayNumber - From.DayNumber + 1;
    public int Width => DayCount * PxPerDay;
    public int Height => HeaderHeight + Math.Max(Rows.Count, 1) * RowHeight;
    public bool ShowDayNumbers => PxPerDay >= 20;
    public bool ShowWeekMarks => PxPerDay >= 6;
    public bool TodayVisible => Today >= From && Today <= To;
    public int TodayX => X(Today);
    public int DatedCount => Rows.Count(r => r.Bar is not null || r.Milestone is not null);
    public int X(DateOnly d) => (d.DayNumber - From.DayNumber) * PxPerDay;
    public static int RowY(int index) => HeaderHeight + index * RowHeight;

    public static GanttChart Build(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency> links, DateOnly today, bool hideClosed)
    {
        var all = tasks.Where(t => !hideClosed || t.IsOpen).ToList();
        var byId = all.ToDictionary(t => t.Id);
        var children = all.Where(t => t.ParentTaskId is Guid p && byId.ContainsKey(p)).ToLookup(t => t.ParentTaskId!.Value);
        var linkList = links.ToList();

        // Tree order: a parent before its subtasks, siblings by first planned date then title.
        var ordered = new List<(TaskItem Task, int Depth)>();
        var visited = new HashSet<Guid>();
        void Visit(TaskItem t, int depth)
        {
            if (!visited.Add(t.Id) || depth > 64) return;
            ordered.Add((t, depth));
            foreach (var c in Sorted(children[t.Id])) Visit(c, depth + 1);
        }
        foreach (var root in Sorted(all.Where(t => t.ParentTaskId is null || !byId.ContainsKey(t.ParentTaskId.Value))))
            Visit(root, 0);

        // Summary spans: the earliest and latest planned date anywhere below a parent, derived from its children.
        var spans = new Dictionary<Guid, (DateOnly? Start, DateOnly? End)>();
        (DateOnly? Start, DateOnly? End) Span(TaskItem t)
        {
            if (spans.TryGetValue(t.Id, out var known)) return known;
            DateOnly? s = null, e = null;
            foreach (var c in children[t.Id])
            {
                var (cs, ce) = Span(c);
                foreach (var v in new[] { c.StartDate ?? c.DueDate, cs })
                    if (v is DateOnly d && (s is null || d < s)) s = d;
                foreach (var v in new[] { c.DueDate ?? c.StartDate, ce })
                    if (v is DateOnly d && (e is null || d > e)) e = d;
            }
            return spans[t.Id] = (s, e);
        }
        foreach (var (t, _) in ordered) Span(t);

        // The axis: everything planned, plus today, with a little room either side.
        var dates = new List<DateOnly> { today };
        foreach (var (t, _) in ordered)
        {
            if (t.StartDate is DateOnly a) dates.Add(a);
            if (t.DueDate is DateOnly b) dates.Add(b);
            var (s, e) = spans[t.Id];
            if (s is DateOnly c) dates.Add(c);
            if (e is DateOnly d) dates.Add(d);
        }
        var anyPlanned = dates.Count > 1;
        var from = (anyPlanned ? dates.Min() : today.AddDays(-7)).AddDays(-PaddingDays);
        var to = (anyPlanned ? dates.Max() : today.AddDays(21)).AddDays(PaddingDays);
        var dayCount = to.DayNumber - from.DayNumber + 1;
        var px = dayCount <= 45 ? 32 : dayCount <= 100 ? 24 : dayCount <= 200 ? 14 : dayCount <= 400 ? 8 : 5;
        int X(DateOnly d) => (d.DayNumber - from.DayNumber) * px;
        GanttBar Bar(DateOnly s, DateOnly e) => new(s, e, X(s), Math.Max(4, (e.DayNumber - s.DayNumber + 1) * px - 2));

        var rows = new List<GanttRow>();
        foreach (var (t, depth) in ordered)
        {
            GanttBar? bar = null, milestone = null, summary = null;
            if (t.StartDate is DateOnly s1 && t.DueDate is DateOnly e1) bar = Bar(s1, e1 < s1 ? s1 : e1);
            else if (t.StartDate is DateOnly s2) bar = Bar(s2, s2);
            else if (t.DueDate is DateOnly e2) milestone = Bar(e2, e2);
            var childCount = children[t.Id].Count();
            if (childCount > 0 && spans[t.Id] is (DateOnly ss, DateOnly se)) summary = Bar(ss, se);
            rows.Add(new GanttRow(t, depth, rows.Count, bar, milestone, summary, t.IsOverdue(today), childCount,
                "status-" + t.Status.ToString().ToLowerInvariant()));
        }

        var (criticalIds, drivingLinks, cpmEnd) = CriticalPath(rows, linkList, from);
        for (var i = 0; i < rows.Count; i++)
            if (criticalIds.Contains(rows[i].Task.Id)) rows[i] = rows[i] with { Critical = true };
        var rowById = rows.ToDictionary(r => r.Task.Id);

        // Arrows: from the predecessor's finish (FS, FF) or start (SS, SF) to the successor's start (FS, SS) or finish (FF, SF).
        var arrows = new List<GanttArrow>();
        var undrawn = new List<TaskDependency>();
        foreach (var link in linkList)
        {
            if (!rowById.TryGetValue(link.PredecessorTaskId, out var pred) || !rowById.TryGetValue(link.SuccessorTaskId, out var succ)
                || (pred.Bar ?? pred.Milestone) is null || (succ.Bar ?? succ.Milestone) is null)
            {
                if (byId.ContainsKey(link.PredecessorTaskId) && byId.ContainsKey(link.SuccessorTaskId)) undrawn.Add(link);
                continue;
            }
            var exitAtFinish = link.Type.WaitsForFinish();
            var enterAtStart = link.Type.GatesStart();
            var (x1, y1) = End(pred, exitAtFinish);
            var (x2, y2) = End(succ, !enterAtStart);
            var ax = x1 + (exitAtFinish ? 8 : -8);
            var bx = x2 + (enterAtStart ? -8 : 8);
            var direct = enterAtStart ? ax <= bx : ax >= bx;
            var path = direct
                ? $"M{x1},{y1} H{ax} V{y2} H{x2}"
                : $"M{x1},{y1} H{ax} V{MidY(pred, succ)} H{bx} V{y2} H{x2}";
            var conflict = DependencyRules.HasDateConflict(link.Type, link.LagDays, pred.Task, succ.Task);
            var critical = drivingLinks.Contains(link.Id);
            var lag = link.LagDays == 0 ? string.Empty : $", lag {Ui.Lag(link.LagDays)}";
            arrows.Add(new GanttArrow(link, path, conflict,
                $"\"{succ.Task.Title}\" waits on \"{pred.Task.Title}\" ({link.Type.Code()} - {link.Type.Label()}{lag})"
                + (conflict ? " - planned dates conflict" : string.Empty) + (critical ? " - on the critical path" : string.Empty))
            { Critical = critical });
        }

        // The axis labels.
        var days = new List<GanttDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
            days.Add(new GanttDay(d, X(d), d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, d.DayOfWeek == DayOfWeek.Monday));
        var months = days.GroupBy(d => (d.Date.Year, d.Date.Month))
            .Select(g => new GanttMonth(
                g.Count() * px >= 64 ? g.First().Date.ToString("MMM yyyy") : g.First().Date.ToString("MMM"),
                g.First().Left, g.Count() * px))
            .ToList();

        return new GanttChart
        {
            From = from, To = to, Today = today, PxPerDay = px,
            Rows = rows, Arrows = arrows, Months = months, Days = days, UndrawnLinks = undrawn,
            CriticalIds = criticalIds,
            CriticalEnd = cpmEnd > 0 ? from.AddDays(cpmEnd - 1) : null
        };

        static IEnumerable<TaskItem> Sorted(IEnumerable<TaskItem> ts) => ts
            .OrderBy(t => (t.StartDate ?? t.DueDate) is null).ThenBy(t => t.StartDate ?? t.DueDate).ThenBy(t => t.Title);

        static (int X, int Y) End(GanttRow r, bool finish)
        {
            var b = (r.Bar ?? r.Milestone)!;
            return (finish ? b.Left + b.Width : b.Left, RowY(r.Index) + BarTop + BarHeight / 2);
        }

        /// The row edge an arrow detours along when the successor's end lies left of the predecessor's.
        static int MidY(GanttRow pred, GanttRow succ) => succ.Index > pred.Index ? RowY(pred.Index) + RowHeight : RowY(pred.Index);
    }

    /// <summary>
    /// The critical path (spec §6.16): a standard forward/backward pass over the drawn tasks. Durations come from the
    /// planned dates (one day for a single-date task), each planned start is an earliest-start constraint, all four link
    /// types and lags are honoured, and cancelled tasks are left out. Zero total float = critical; a link is "driving"
    /// when it is what fixes its successor's earliest date. Returns the critical task ids, the driving link ids between
    /// critical tasks, and the project end as a day offset from <paramref name="from"/> (exclusive).
    /// </summary>
    private static (HashSet<Guid> Critical, HashSet<Guid> DrivingLinks, int End) CriticalPath(IReadOnlyList<GanttRow> rows, IReadOnlyList<TaskDependency> links, DateOnly from)
    {
        var nodes = rows.Where(r => (r.Bar ?? r.Milestone) is not null && r.Task.Status != TaskItemStatus.Cancelled).ToList();
        var n = nodes.Count;
        if (n == 0) return ([], [], 0);
        var index = new Dictionary<Guid, int>();
        var dur = new int[n];
        var planned = new int[n];
        for (var i = 0; i < n; i++)
        {
            index[nodes[i].Task.Id] = i;
            var b = (nodes[i].Bar ?? nodes[i].Milestone)!;
            dur[i] = b.End.DayNumber - b.Start.DayNumber + 1;
            planned[i] = b.Start.DayNumber - from.DayNumber;
        }
        var edges = links
            .Where(l => index.ContainsKey(l.PredecessorTaskId) && index.ContainsKey(l.SuccessorTaskId))
            .Select(l => (l.Id, P: index[l.PredecessorTaskId], S: index[l.SuccessorTaskId], l.Type, Lag: l.LagDays))
            .ToList();

        // Topological order. TaskStructureService keeps the graph acyclic; if it ever weren't, bail out rather than loop.
        var indegree = new int[n];
        foreach (var e in edges) indegree[e.S]++;
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => indegree[i] == 0));
        var order = new List<int>(n);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            order.Add(u);
            foreach (var e in edges)
                if (e.P == u && --indegree[e.S] == 0) queue.Enqueue(e.S);
        }
        if (order.Count < n) return ([], [], 0);

        // Forward pass: earliest start/finish (finish exclusive), never earlier than the planned start.
        var es = new int[n];
        var ef = new int[n];
        foreach (var u in order)
        {
            es[u] = planned[u];
            foreach (var e in edges)
            {
                if (e.S != u) continue;
                var need = e.Type switch
                {
                    DependencyType.FinishToStart => ef[e.P] + e.Lag,
                    DependencyType.StartToStart => es[e.P] + e.Lag,
                    DependencyType.FinishToFinish => ef[e.P] + e.Lag - dur[u],
                    _ => es[e.P] + e.Lag - dur[u] // StartToFinish
                };
                if (need > es[u]) es[u] = need;
            }
            ef[u] = es[u] + dur[u];
        }
        var end = ef.Max();

        // Backward pass: latest finish/start, working back from the project end.
        var lf = new int[n];
        var ls = new int[n];
        Array.Fill(lf, end);
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var u = order[k];
            foreach (var e in edges)
            {
                if (e.P != u) continue;
                var s = e.S;
                var limit = e.Type switch
                {
                    DependencyType.FinishToStart => ls[s] - e.Lag,
                    DependencyType.StartToStart => ls[s] - e.Lag + dur[u],
                    DependencyType.FinishToFinish => lf[s] - e.Lag,
                    _ => lf[s] - e.Lag + dur[u] // StartToFinish
                };
                if (limit < lf[u]) lf[u] = limit;
            }
            ls[u] = lf[u] - dur[u];
        }

        var critical = new HashSet<Guid>();
        for (var i = 0; i < n; i++)
            if (ls[i] - es[i] <= 0) critical.Add(nodes[i].Task.Id);
        var driving = new HashSet<Guid>();
        foreach (var e in edges)
        {
            if (!critical.Contains(nodes[e.P].Task.Id) || !critical.Contains(nodes[e.S].Task.Id)) continue;
            var tight = e.Type switch
            {
                DependencyType.FinishToStart => ef[e.P] + e.Lag == es[e.S],
                DependencyType.StartToStart => es[e.P] + e.Lag == es[e.S],
                DependencyType.FinishToFinish => ef[e.P] + e.Lag == ef[e.S],
                _ => es[e.P] + e.Lag == ef[e.S]
            };
            if (tight) driving.Add(e.Id);
        }
        return (critical, driving, end);
    }
}

/// <summary>One line of the chart. A task has at most one of <see cref="Bar"/> / <see cref="Milestone"/>; a parent may also have a <see cref="Summary"/>.</summary>
public sealed record GanttRow(TaskItem Task, int Depth, int Index, GanttBar? Bar, GanttBar? Milestone, GanttBar? Summary, bool Overdue, int ChildCount, string StatusClass)
{
    public int Y => GanttChart.RowY(Index);
    /// <summary>On the critical path (§6.16).</summary>
    public bool Critical { get; init; }
}

public sealed record GanttBar(DateOnly Start, DateOnly End, int Left, int Width);

public sealed record GanttArrow(TaskDependency Link, string Path, bool Conflict, string Title)
{
    /// <summary>A driving link between two critical tasks.</summary>
    public bool Critical { get; init; }
}

public sealed record GanttMonth(string Label, int Left, int Width);

public sealed record GanttDay(DateOnly Date, int Left, bool Weekend, bool WeekStart);

using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application.Scheduling;

/// <summary>
/// Critical path analysis (spec §6.17) as a pure, deterministic function of a project's tasks, links, working
/// calendar, target date and required buffer. Promoted from the Gantt's per-load pass: the same forward/backward
/// critical-path method, now in working days, keeping every float value, comparing the result with the target date
/// and the required project buffer, validating the plan first, and assessing early-completion opportunities.
/// <list type="bullet">
/// <item>Each task's <b>Schedule Span</b> is its planned Start..Due window in working days (one day for a single-date
/// task); the estimate is never a duration.</item>
/// <item>Each planned start is an earliest-start constraint, as the Gantt always treated it (§6.16).</item>
/// <item>Links use the same day convention as the dependency date check (§6.15): an FS successor may start on the day
/// its predecessor is due (plus lag); an SF successor may be due on the day its predecessor starts (plus lag).
/// Lag stays calendar days; a constraint landing on a non-working day rolls to the next working day.</item>
/// <item>Tasks with dates but no links to other dated tasks are standalone activities, not paths of length one.</item>
/// <item><c>Total Float &lt;= 0</c> is critical; negative float is kept, never clamped.</item>
/// </list>
/// </summary>
public static class CriticalPathEngine
{
    /// <summary>Critical paths enumerated at most; the rest are counted as omitted.</summary>
    public const int MaxPaths = 25;
    /// <summary>A planning window at least this many times the estimate's working days, and at least <see cref="WideWindowMinDays"/> wide, is "unusually wide".</summary>
    public const int WideWindowRatio = 3;
    public const int WideWindowMinDays = 5;
    private const int MaxPathSteps = 20_000;

    private sealed class Node(TaskItem task, DateOnly first, DateOnly last, int span, int plannedEs)
    {
        public TaskItem Task { get; } = task;
        /// <summary>First and last working day of the planned window.</summary>
        public DateOnly First { get; } = first;
        public DateOnly Last { get; } = last;
        public int Span { get; } = span;
        public int PlannedEs { get; } = plannedEs;
        public int Index { get; set; } = -1;
        public bool InNetwork => Index >= 0;
    }

    private sealed record Edge(int Index, TaskDependency Link, int P, int S);

    public static CriticalPathResult Analyse(CriticalPathInput input)
    {
        var cal = input.Calendar;
        var opt = input.Options;
        var thresholds = new AnalysisThresholds(Math.Max(0, opt.NearCriticalThresholdWorkingDays), opt.BufferAmberPercent, opt.BufferRedPercent, Math.Max(1, opt.HoursPerWorkingDay));
        var runAt = DateTime.UtcNow;
        var tasks = input.Tasks.ToList();
        var byId = tasks.ToDictionary(t => t.Id);
        var errors = new List<PlanIssue>();
        var warnings = new List<PlanIssue>();
        var fingerprint = ScheduleFingerprint.Compute(tasks, input.Links, input.TargetDate, input.RequiredBufferWorkingDays, cal);

        // ---- 1. Structure: every link joins two tasks of the project; the graph is acyclic.
        var validLinks = new List<TaskDependency>();
        var badLinks = new List<Guid>();
        foreach (var l in input.Links)
        {
            if (l.PredecessorTaskId == l.SuccessorTaskId || !byId.ContainsKey(l.PredecessorTaskId) || !byId.ContainsKey(l.SuccessorTaskId)) badLinks.Add(l.Id);
            else validLinks.Add(l);
        }
        if (badLinks.Count > 0)
            errors.Add(new PlanIssue(PlanIssueCodes.InvalidReference,
                $"{Count(badLinks.Count, "dependency link")} refer{(badLinks.Count == 1 ? "s" : "")} to a task that is not on this project. Remove {(badLinks.Count == 1 ? "it" : "them")} before analysing.", [], badLinks));

        var live = tasks.Where(t => t.Status != TaskItemStatus.Cancelled).ToList();
        var liveIds = live.Select(t => t.Id).ToHashSet();
        var liveLinks = validLinks.Where(l => liveIds.Contains(l.PredecessorTaskId) && liveIds.Contains(l.SuccessorTaskId)).ToList();
        var cycle = CycleMembers(live, liveLinks);
        if (cycle.Count > 0)
            errors.Add(new PlanIssue(PlanIssueCodes.CircularDependency,
                $"Circular dependency between {Titles(cycle.Select(id => byId[id]))}. A critical path can't be calculated until the loop is broken.",
                cycle, liveLinks.Where(l => cycle.Contains(l.PredecessorTaskId) && cycle.Contains(l.SuccessorTaskId)).Select(l => l.Id).ToList()));

        // ---- 2. Scheduled tasks.
        var scheduled = live.Where(t => t.StartDate is not null || t.DueDate is not null).ToList();
        var unscheduled = live.Where(t => t.StartDate is null && t.DueDate is null && t.IsOpen).ToList();
        if (unscheduled.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.Unscheduled,
                $"{Count(unscheduled.Count, "open task")} {Are(unscheduled.Count)} unscheduled (no start or due date): {Titles(unscheduled)}.", Ids(unscheduled), []));
        if (scheduled.Count == 0)
            errors.Add(new PlanIssue(PlanIssueCodes.NoScheduledTasks, "No task on this project has a start or due date, so there is nothing to analyse.", [], []));
        if (errors.Count > 0)
            return new CriticalPathResult { RunAt = runAt, ProjectId = input.ProjectId, Errors = errors, Warnings = warnings, Thresholds = thresholds, InputFingerprint = fingerprint };

        // ---- 3. Schedule spans in working days.
        var nodes = new Dictionary<Guid, Node>();
        var onNonWorkingDay = new List<TaskItem>();
        var holidayHits = new List<string>();
        var holidayTasks = new List<Guid>();
        foreach (var t in scheduled)
        {
            DateOnly first, last;
            if (t.StartDate is DateOnly s && t.DueDate is DateOnly d)
            {
                first = cal.Ceil(s);
                last = cal.Floor(d < s ? s : d);
                if (last < first) last = first; // a window lying entirely on non-working days still takes one working day
            }
            else if (t.StartDate is DateOnly s2) first = last = cal.Ceil(s2);
            else first = last = cal.Floor(t.DueDate!.Value);
            var days = cal.Ordinal(last) - cal.Ordinal(first) + 1;
            nodes[t.Id] = new Node(t, first, last, days, cal.Ordinal(first));

            if ((t.StartDate is DateOnly a && !cal.IsWorking(a)) || (t.DueDate is DateOnly b && !cal.IsWorking(b))) onNonWorkingDay.Add(t);
            if (t.StartDate is DateOnly ws && t.DueDate is DateOnly we && we >= ws)
            {
                var holidays = cal.ExceptionsBetween(ws, we).Where(x => !x.IsWorking).ToList();
                if (holidays.Count > 0)
                {
                    holidayTasks.Add(t.Id);
                    holidayHits.Add($"\"{t.Title}\" includes {string.Join(", ", holidays.Select(h => $"{h.Date:d MMM yyyy} ({h.Name})"))}");
                }
            }
        }
        if (onNonWorkingDay.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.NonWorkingDay,
                $"{Count(onNonWorkingDay.Count, "task")} start{(onNonWorkingDay.Count == 1 ? "s" : "")} or {(onNonWorkingDay.Count == 1 ? "is" : "are")} due on a non-working day: {Titles(onNonWorkingDay)}. The analysis uses the nearest working day.",
                Ids(onNonWorkingDay), []));
        if (holidayHits.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.HolidayInWindow,
                $"{Count(holidayHits.Count, "planned window")} include{(holidayHits.Count == 1 ? "s" : "")} a non-working day: {string.Join("; ", holidayHits)}.", holidayTasks, []));

        // ---- 4. The dependency network: dated, non-cancelled tasks joined by links. Links to undated tasks are left out.
        var scheduledIds = nodes.Keys.ToHashSet();
        var netLinks = liveLinks.Where(l => scheduledIds.Contains(l.PredecessorTaskId) && scheduledIds.Contains(l.SuccessorTaskId)).ToList();
        var dropped = liveLinks.Where(l => !scheduledIds.Contains(l.PredecessorTaskId) || !scheduledIds.Contains(l.SuccessorTaskId)).ToList();
        if (dropped.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.LinkNotAnalysed,
                $"{Count(dropped.Count, "link")} {(dropped.Count == 1 ? "was" : "were")} left out because a task in the chain has no dates: {string.Join("; ", dropped.Select(l => Describe(l, byId)))}.",
                dropped.SelectMany(l => new[] { l.PredecessorTaskId, l.SuccessorTaskId }).Where(id => !scheduledIds.Contains(id)).Distinct().ToList(),
                dropped.Select(l => l.Id).ToList()));

        var linkedIds = netLinks.SelectMany(l => new[] { l.PredecessorTaskId, l.SuccessorTaskId }).ToHashSet();
        var network = scheduled.Where(t => linkedIds.Contains(t.Id)).ToList();
        var standalone = scheduled.Where(t => !linkedIds.Contains(t.Id)).ToList();
        for (var i = 0; i < network.Count; i++) nodes[network[i].Id].Index = i;
        var n = network.Count;
        var span = network.Select(t => nodes[t.Id].Span).ToArray();
        var edges = netLinks.Select((l, i) => new Edge(i, l, nodes[l.PredecessorTaskId].Index, nodes[l.SuccessorTaskId].Index)).ToList();
        var incoming = new List<Edge>[n];
        var outgoing = new List<Edge>[n];
        for (var i = 0; i < n; i++) { incoming[i] = []; outgoing[i] = []; }
        foreach (var e in edges) { incoming[e.S].Add(e); outgoing[e.P].Add(e); }

        // ---- 5. Forward and backward passes (the graph is acyclic: step 1 checked the whole project).
        var indegree = new int[n];
        foreach (var e in edges) indegree[e.S]++;
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => indegree[i] == 0));
        var order = new List<int>(n);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            order.Add(u);
            foreach (var e in outgoing[u]) if (--indegree[e.S] == 0) queue.Enqueue(e.S);
        }

        var es = new int[n]; var ef = new int[n]; var ls = new int[n]; var lf = new int[n];
        foreach (var u in order)
        {
            es[u] = nodes[network[u].Id].PlannedEs;
            foreach (var e in incoming[u])
            {
                var constraint = Constraint(cal, e.Link, es[e.P], ef[e.P]);
                var needEs = e.Link.Type.GatesStart() ? constraint : constraint - span[u] + 1;
                if (needEs > es[u]) es[u] = needEs;
            }
            ef[u] = es[u] + span[u] - 1;
        }
        var end = n > 0 ? ef.Max() : 0;
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var u = order[k];
            lf[u] = end;
            foreach (var e in outgoing[u])
            {
                var limit = Limit(cal, e.Link, ls[e.S], lf[e.S]);
                var lfLimit = e.Link.Type.WaitsForFinish() ? limit : limit + span[u] - 1;
                if (lfLimit < lf[u]) lf[u] = lfLimit;
            }
            ls[u] = lf[u] - span[u] + 1;
        }

        // ---- 6. Float, criticality, driving links.
        var tf = new int[n]; var ff = new int[n];
        var critical = new bool[n]; var near = new bool[n];
        var tight = new bool[edges.Count]; var slack = new int[edges.Count];
        foreach (var e in edges)
        {
            var constraint = Constraint(cal, e.Link, es[e.P], ef[e.P]);
            var gated = e.Link.Type.GatesStart() ? es[e.S] : ef[e.S];
            slack[e.Index] = gated - constraint;
            tight[e.Index] = slack[e.Index] == 0;
        }
        for (var u = 0; u < n; u++)
        {
            tf[u] = ls[u] - es[u];
            ff[u] = outgoing[u].Count == 0 ? end - ef[u] : outgoing[u].Min(e => slack[e.Index]);
            critical[u] = tf[u] <= 0;
            near[u] = tf[u] > 0 && tf[u] <= thresholds.NearCriticalThresholdWorkingDays;
        }
        var driving = edges.Where(e => tight[e.Index] && critical[e.P] && critical[e.S]).ToList();
        var drivingOut = new List<Edge>[n];
        var drivingInCount = new int[n];
        for (var i = 0; i < n; i++) drivingOut[i] = [];
        foreach (var e in driving) { drivingOut[e.P].Add(e); drivingInCount[e.S]++; }

        // ---- 7. Critical paths: every connected driving sequence, source to sink.
        var paths = new List<List<int>>();
        var omitted = 0;
        var steps = 0;
        var current = new List<int>();
        void Walk(int u)
        {
            if (++steps > MaxPathSteps) return;
            current.Add(u);
            if (drivingOut[u].Count == 0)
            {
                if (paths.Count < MaxPaths) paths.Add([.. current]); else omitted++;
            }
            else
            {
                foreach (var e in drivingOut[u]) Walk(e.S);
            }
            current.RemoveAt(current.Count - 1);
        }
        for (var u = 0; u < n; u++) if (critical[u] && drivingInCount[u] == 0) Walk(u);
        var chains = paths
            .OrderByDescending(p => ef[p[^1]]).ThenByDescending(p => p.Count).ThenBy(p => network[p[0]].Title)
            .Select(p => new CriticalPathChain(p.Select(i => network[i].Id).ToList(), p.Select(i => network[i].Title).ToList(), cal.Date(es[p[0]]), cal.Date(ef[p[^1]])))
            .ToList();

        // ---- 8. Completion dates.
        DateOnly? networkCompletion = n > 0 ? cal.Date(end) : null;
        var plannedCompletion = networkCompletion ?? standalone.Max(t => nodes[t.Id].Last);
        foreach (var t in standalone) if (nodes[t.Id].Last > plannedCompletion) plannedCompletion = nodes[t.Id].Last;
        if (n == 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.NoNetwork,
                "No two scheduled tasks are linked, so there is no critical path. The planned completion is the latest scheduled date.", [], [], Informational: true));
        if (standalone.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.NoDependencies,
                $"{Count(standalone.Count, "scheduled task")} {(standalone.Count == 1 ? "has" : "have")} no dependencies and sit{(standalone.Count == 1 ? "s" : "")} outside the critical path: {Titles(standalone)}.", Ids(standalone), []));
        if (networkCompletion is DateOnly nc)
        {
            var late = standalone.Where(t => nodes[t.Id].Last > nc).OrderByDescending(t => nodes[t.Id].Last).ToList();
            if (late.Count > 0)
                warnings.Add(new PlanIssue(PlanIssueCodes.StandaloneAfterNetwork,
                    $"{Count(late.Count, "task")} without dependencies finish{(late.Count == 1 ? "es" : "")} after the dependency network completes ({nc:d MMM yyyy}) and so set{(late.Count == 1 ? "s" : "")} the planned completion: {Titles(late)} (latest {nodes[late[0].Id].Last:d MMM yyyy}).",
                    Ids(late), []));
        }

        // ---- 9. Target date and project buffer.
        var schedule = Buffer(cal, input.TargetDate, input.RequiredBufferWorkingDays, plannedCompletion, networkCompletion, thresholds, warnings);

        // ---- 10. Other plan-readiness warnings.
        var conflicts = liveLinks.Where(l => DependencyRules.HasDateConflict(l.Type, l.LagDays, byId[l.PredecessorTaskId], byId[l.SuccessorTaskId])).ToList();
        if (conflicts.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.DateConflict,
                $"{Count(conflicts.Count, "dependency date conflict")}: {string.Join("; ", conflicts.Select(l => Describe(l, byId)))}.",
                conflicts.SelectMany(l => new[] { l.PredecessorTaskId, l.SuccessorTaskId }).Distinct().ToList(), conflicts.Select(l => l.Id).ToList()));

        var wide = scheduled.Where(t => t.IsOpen && t.StartDate is not null && t.DueDate is not null && EstimateDays(t, thresholds) is int ed
                && nodes[t.Id].Span >= WideWindowRatio * ed && nodes[t.Id].Span >= WideWindowMinDays).ToList();
        if (wide.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.WideWindow,
                $"{Count(wide.Count, "task")} {(wide.Count == 1 ? "has" : "have")} an unusually wide planning window for {(wide.Count == 1 ? "its" : "their")} estimate: {string.Join("; ", wide.Select(t => $"\"{t.Title}\" ({nodes[t.Id].Span} working days for {TimeFormatMinutes(t.EstimateMinutes!.Value)})"))}.",
                Ids(wide), []));

        var children = live.Where(t => t.ParentTaskId is Guid p && byId.ContainsKey(p)).ToLookup(t => t.ParentTaskId!.Value);
        var spans = new Dictionary<Guid, (DateOnly? Start, DateOnly? End)>();
        (DateOnly? Start, DateOnly? End) ChildSpan(TaskItem t)
        {
            if (spans.TryGetValue(t.Id, out var known)) return known;
            DateOnly? s = null, e = null;
            foreach (var c in children[t.Id])
            {
                var (cs, ce) = ChildSpan(c);
                foreach (var v in new[] { c.StartDate ?? c.DueDate, cs }) if (v is DateOnly x && (s is null || x < s)) s = x;
                foreach (var v in new[] { c.DueDate ?? c.StartDate, ce }) if (v is DateOnly x && (e is null || x > e)) e = x;
            }
            return spans[t.Id] = (s, e);
        }
        var parentsOff = scheduled.Where(t => children.Contains(t.Id) && t.StartDate is DateOnly ps && t.DueDate is DateOnly pe
                && ChildSpan(t) is var (cs, ce) && ((cs is DateOnly a && a < ps) || (ce is DateOnly b && b > pe))).ToList();
        if (parentsOff.Count > 0)
            warnings.Add(new PlanIssue(PlanIssueCodes.ParentWindow,
                $"{Count(parentsOff.Count, "parent task")} {(parentsOff.Count == 1 ? "has" : "have")} subtasks planned outside {(parentsOff.Count == 1 ? "its" : "their")} own dates: {Titles(parentsOff)}.", Ids(parentsOff), []));

        // ---- 11. Early completion and recovery opportunities (informational; nothing moves).
        var opportunities = new List<EarlyCompletionOpportunity>();
        for (var u = 0; u < n; u++)
        {
            var t = network[u];
            if (!t.IsOpen || t.StartDate is null || t.DueDate is null || EstimateDays(t, thresholds) is not int estDays || estDays >= span[u]) continue;
            var successors = new List<SuccessorReadiness>();
            if (critical[u])
            {
                foreach (var e in drivingOut[u])
                {
                    var s = network[e.S];
                    var holders = incoming[e.S].Where(o => o.Index != e.Index && tight[o.Index]).Select(o => network[o.P].Title).ToList();
                    var otherGates = liveLinks.Where(l => l.SuccessorTaskId == s.Id && l.Id != e.Link.Id).ToList();
                    var ready = otherGates.All(l => DependencyRules.IsMet(l.Type, byId[l.PredecessorTaskId].Status));
                    var canPropagate = holders.Count == 0;
                    var readiness = !canPropagate ? $"Still held by {Titles(holders)}: finishing early would not release it."
                        : ready ? "Could be released earlier."
                        : "Could be released earlier once its other links are met.";
                    successors.Add(new SuccessorReadiness(s.Id, s.Title, s.Assignee?.DisplayName, canPropagate, ready, readiness, "Requires management confirmation."));
                }
                if (drivingOut[u].Count == 0)
                    successors.Add(new SuccessorReadiness(null, null, null, true, true, "No downstream task: the planned completion itself would move earlier.", "Not applicable."));
            }
            var recovery = critical[u] && successors.Any(s => s.CanPropagate);
            var action = recovery
                ? "Only expedite this task if the downstream work can be brought forward: confirm the assignee's earlier availability first."
                : critical[u] ? "Expediting this task would not advance the project while another predecessor still controls its successor."
                : "Not on the critical path: finishing early would not change the planned completion.";
            opportunities.Add(new EarlyCompletionOpportunity(t.Id, t.Title, t.Assignee?.DisplayName, t.EstimateMinutes!.Value, estDays, span[u], span[u] - estDays,
                critical[u], recovery, successors, action));
        }
        opportunities = opportunities
            .OrderByDescending(o => o.IsRecoveryOpportunity).ThenByDescending(o => o.IsCritical).ThenByDescending(o => o.PotentialDays).ThenBy(o => o.Title)
            .ToList();

        var earlyCompletions = new List<EarlyCompletionNote>();
        for (var u = 0; u < n; u++)
        {
            var t = network[u];
            if (!critical[u] || t.Status != TaskItemStatus.Done || t.CompletedAt is not DateTime completedAt || t.DueDate is not DateOnly due) continue;
            var completedOn = DateOnly.FromDateTime(completedAt);
            if (completedOn >= due) continue;
            foreach (var e in drivingOut[u].Where(e => network[e.S].Status == TaskItemStatus.Todo))
                earlyCompletions.Add(new EarlyCompletionNote(t.Id, t.Title, completedOn, due, network[e.S].Id, network[e.S].Title));
        }

        // ---- 12. Assemble.
        var rows = new List<TaskAnalysis>();
        for (var u = 0; u < n; u++)
        {
            var t = network[u];
            rows.Add(new TaskAnalysis(t.Id, t.Title, t.Status, t.Assignee?.DisplayName, t.StartDate, t.DueDate,
                cal.Date(es[u]), cal.Date(ef[u]), cal.Date(ls[u]), cal.Date(lf[u]), tf[u], ff[u], span[u], true, critical[u], near[u]));
        }
        foreach (var t in standalone)
        {
            var node = nodes[t.Id];
            rows.Add(new TaskAnalysis(t.Id, t.Title, t.Status, t.Assignee?.DisplayName, t.StartDate, t.DueDate,
                node.First, node.Last, null, null, null, null, node.Span, false, false, false));
        }
        rows = rows.OrderBy(r => r.EarlyStart).ThenBy(r => r.EarlyFinish).ThenBy(r => r.Title).ToList();

        return new CriticalPathResult
        {
            RunAt = runAt,
            ProjectId = input.ProjectId,
            Errors = [],
            Warnings = warnings,
            Schedule = schedule,
            Tasks = rows,
            DrivingLinkIds = driving.Select(e => e.Link.Id).ToList(),
            CriticalPaths = chains,
            CriticalPathsOmitted = omitted,
            Opportunities = opportunities,
            EarlyCompletions = earlyCompletions,
            Thresholds = thresholds,
            InputFingerprint = fingerprint
        };
    }

    /// <summary>
    /// The earliest ordinal the successor's gated end may take under this link, given the predecessor's early start
    /// and finish: FS/SS constrain the successor's start, FF/SF its finish; the anchor is the predecessor's finish
    /// (FS, FF) or start (SS, SF), plus the lag in calendar days, rolled forward to a working day.
    /// </summary>
    private static int Constraint(WorkDayCalendar cal, TaskDependency link, int predecessorEs, int predecessorEf)
    {
        var anchor = link.Type.WaitsForFinish() ? predecessorEf : predecessorEs;
        return cal.Ordinal(cal.Ceil(cal.Date(anchor).AddDays(link.LagDays)));
    }

    /// <summary>The inverse: the latest ordinal the predecessor's tied end may take, given the successor's late start and finish.</summary>
    private static int Limit(WorkDayCalendar cal, TaskDependency link, int successorLs, int successorLf)
    {
        var anchor = link.Type.GatesStart() ? successorLs : successorLf;
        return cal.Ordinal(cal.Floor(cal.Date(anchor).AddDays(-link.LagDays)));
    }

    private static ScheduleSummary Buffer(WorkDayCalendar cal, DateOnly? targetDate, int? requiredBuffer, DateOnly plannedCompletion, DateOnly? networkCompletion,
        AnalysisThresholds thresholds, List<PlanIssue> warnings)
    {
        if (targetDate is not DateOnly target)
        {
            warnings.Add(new PlanIssue(PlanIssueCodes.NoTargetDate, "The project has no target date, so the project buffer can't be calculated.", [], [], Informational: true));
            return new ScheduleSummary
            {
                PlannedCompletion = plannedCompletion, NetworkCompletion = networkCompletion, RequiredBufferDays = requiredBuffer,
                BufferStatus = BufferStatus.NotAvailable, Note = "Project buffer can't be calculated: the project target date is not set."
            };
        }
        if (!cal.IsWorking(target))
            warnings.Add(new PlanIssue(PlanIssueCodes.TargetNonWorkingDay,
                $"The target date ({target:d MMM yyyy}) is not a working day; the last working day before it ({cal.Floor(target):d MMM yyyy}) is used.", [], []));
        var t = cal.Ordinal(cal.Floor(target));
        var p = cal.Ordinal(plannedCompletion);
        var beyond = Math.Max(0, p - t);
        if (requiredBuffer is null or <= 0)
        {
            warnings.Add(new PlanIssue(PlanIssueCodes.NoRequiredBuffer, "No required project buffer is set on the project, so buffer consumption can't be measured; only the headroom before the target date is shown.", [], [], Informational: true));
            return new ScheduleSummary
            {
                PlannedCompletion = plannedCompletion, NetworkCompletion = networkCompletion, TargetDate = target, InternalCompletion = cal.Floor(target),
                RequiredBufferDays = requiredBuffer, HeadroomDays = t - p, DaysBeyondTarget = beyond,
                BufferStatus = beyond > 0 ? BufferStatus.Red : BufferStatus.Green,
                Note = beyond > 0 ? $"The plan completes {Count(beyond, "working day")} after the target date." : "No required project buffer is set."
            };
        }
        var required = requiredBuffer.Value;
        var internalOrdinal = Math.Max(1, t - required);
        var consumed = Math.Max(0, p - internalOrdinal);
        var remaining = required - consumed;
        var percent = (int)Math.Round(consumed * 100.0 / required);
        var status = beyond > 0 ? BufferStatus.Red
            : percent <= thresholds.BufferAmberPercent ? BufferStatus.Green
            : percent <= thresholds.BufferRedPercent ? BufferStatus.Amber
            : BufferStatus.Red;
        return new ScheduleSummary
        {
            PlannedCompletion = plannedCompletion, NetworkCompletion = networkCompletion, TargetDate = target, InternalCompletion = cal.Date(internalOrdinal),
            RequiredBufferDays = required, BufferConsumedDays = consumed, BufferRemainingDays = remaining, BufferConsumptionPercent = percent,
            HeadroomDays = internalOrdinal - p, DaysBeyondTarget = beyond, BufferStatus = status,
            Note = beyond > 0 ? $"The plan completes {Count(beyond, "working day")} after the target date."
                : consumed == 0 ? $"The full required buffer is protected, with {Count(internalOrdinal - p, "working day")} of additional headroom."
                : null
        };
    }

    /// <summary>
    /// Tasks on a cycle, or between two cycles: what is left after repeatedly stripping tasks with no predecessors
    /// (forward) and tasks with no successors (backward). Empty for an acyclic graph, which TaskStructureService guarantees.
    /// </summary>
    private static List<Guid> CycleMembers(IReadOnlyList<TaskItem> tasks, IReadOnlyList<TaskDependency> links)
    {
        var forward = Strip(tasks, links, l => l.PredecessorTaskId, l => l.SuccessorTaskId);
        if (forward.Count == 0) return [];
        var backward = Strip(tasks, links, l => l.SuccessorTaskId, l => l.PredecessorTaskId);
        return forward.Intersect(backward).ToList();

        static HashSet<Guid> Strip(IReadOnlyList<TaskItem> tasks, IReadOnlyList<TaskDependency> links, Func<TaskDependency, Guid> from, Func<TaskDependency, Guid> to)
        {
            var indegree = tasks.ToDictionary(t => t.Id, _ => 0);
            var outgoing = tasks.ToDictionary(t => t.Id, _ => new List<Guid>());
            foreach (var l in links) { indegree[to(l)]++; outgoing[from(l)].Add(to(l)); }
            var queue = new Queue<Guid>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var remaining = new HashSet<Guid>(indegree.Keys);
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                remaining.Remove(u);
                foreach (var v in outgoing[u]) if (--indegree[v] == 0) queue.Enqueue(v);
            }
            return remaining;
        }
    }

    /// <summary>The estimate as whole working days at the configured hours per day; null without an estimate.</summary>
    private static int? EstimateDays(TaskItem t, AnalysisThresholds thresholds) =>
        t.EstimateMinutes is int m && m > 0 ? Math.Max(1, (int)Math.Ceiling(m / (60.0 * thresholds.HoursPerWorkingDay))) : null;

    private static string Describe(TaskDependency l, IReadOnlyDictionary<Guid, TaskItem> byId)
    {
        var lag = l.LagDays == 0 ? string.Empty : $" {(l.LagDays > 0 ? "+" : "")}{l.LagDays} d";
        return $"\"{byId[l.SuccessorTaskId].Title}\" waits on \"{byId[l.PredecessorTaskId].Title}\" ({l.Type.Code()}{lag})";
    }

    private static string TimeFormatMinutes(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}h" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    private static string Are(int n) => n == 1 ? "is" : "are";
    private static List<Guid> Ids(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Id).ToList();
    private static string Titles(IEnumerable<TaskItem> tasks) => Titles(tasks.Select(t => t.Title));
    private static string Titles(IEnumerable<string> titles) => string.Join(", ", titles.Select(t => $"\"{t}\""));
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Reporting;

namespace Orbit.Pages.Reports;

public class ViewModel(ReportingService reporting, ProjectService projects, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public ReportKind Kind { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }

    /// <summary>reports.view at Department scope fixes the department to the viewer's own (§6.5): the picker is hidden.</summary>
    public bool CanChooseDepartment { get; private set; }
    public ReportDefinition Definition { get; private set; } = null!;
    public IReadOnlyList<PersonCountRow>? CountRows { get; private set; }
    public MeanTimeReport? MeanTime { get; private set; }
    public TimeByPersonReport? TimeByPerson { get; private set; }
    public EstimateAccuracyReport? EstimateAccuracy { get; private set; }
    public ProjectStatusReport? ProjectReport { get; private set; }
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public string FilterSummary { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await RunAsync(ct);
        ProjectItems = (await projects.ListOpenForPickerAsync(includeArchived: true, ct: ct))
            .Select(p => new SelectListItem($"{p.Department.Name} / {p.Name}{(p.Status == ProjectStatus.Archived ? " (archived)" : "")}",
                p.Id.ToString(), p.Id == ProjectId)).ToList();
        if (CanChooseDepartment)
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
    }

    public async Task<IActionResult> OnGetPdfAsync(CancellationToken ct)
    {
        await RunAsync(ct);
        var tables = new List<ReportPdfBuilder.Table>();
        if (CountRows is not null)
        {
            tables.Add(new ReportPdfBuilder.Table(["Person", "Count"],
                CountRows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString()]).ToList()));
        }
        if (MeanTime is not null)
        {
            var rows = MeanTime.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString(), DurationFormat.Hours(r.AverageHours)]).ToList();
            rows.Add(["Overall", MeanTime.OverallCount.ToString(), DurationFormat.Hours(MeanTime.OverallAverageHours)]);
            tables.Add(new ReportPdfBuilder.Table(["Assignee", "Tasks", "Average"], rows));
        }
        if (TimeByPerson is not null) tables.AddRange(TimeByPersonTables(TimeByPerson));
        if (EstimateAccuracy is not null) tables.AddRange(EstimateAccuracyTables(EstimateAccuracy));
        if (ProjectReport is not null) tables.AddRange(ProjectStatusTables(ProjectReport));
        var bytes = ReportPdfBuilder.Build(Definition.Title, FilterSummary, tables, landscape: ProjectReport is not null);
        var fileName = $"orbit-{Kind.ToString().ToLowerInvariant()}-{From:yyyyMMdd}-{To:yyyyMMdd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // The estimate columns show "-" rather than "0m" when none of the tasks has an estimate.
    public static string Estimated(EstimateComparison c) => c.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(c.EstimatedMinutes);
    public static string Actual(EstimateComparison c) => c.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(c.ActualMinutes);

    /// <summary>"+1h 30m (+23%)", or "-" when nothing is estimated.</summary>
    public static string Variance(EstimateComparison c) =>
        c.VariancePercent is double p ? $"{TimeFormat.Signed(c.VarianceMinutes)} ({p:+0;-0;0}%)" : "-";

    public static string Estimate(int? minutes) => minutes is int m ? TimeFormat.Minutes(m) : "-";

    /// <summary>The note beside a task line: Cancelled, No estimate, Over estimate, or nothing.</summary>
    public static string Marker(TaskItemStatus status, int? estimate, bool isOver) =>
        status == TaskItemStatus.Cancelled ? "Cancelled" : estimate is null ? "No estimate" : isOver ? "Over estimate" : "";

    private static string Tasks(int count, int unestimated) => unestimated == 0 ? count.ToString() : $"{count} ({unestimated} unestimated)";

    private static IEnumerable<ReportPdfBuilder.Table> TimeByPersonTables(TimeByPersonReport r)
    {
        var rows = r.Rows.Select(p => (IReadOnlyList<string>)
            [p.Name, TimeFormat.Minutes(p.LoggedMinutes), Tasks(p.TaskCount, p.UnestimatedTasks),
             Estimated(p.Estimate), Actual(p.Estimate), Variance(p.Estimate)]).ToList();
        if (rows.Count > 0)
            rows.Add(["Total", TimeFormat.Minutes(r.TotalLoggedMinutes), Tasks(r.TotalTasks, r.TotalUnestimatedTasks),
                Estimated(r.Total), Actual(r.Total), Variance(r.Total)]);
        yield return new(["Person", "Logged", "Tasks", "Estimated", "Actual to date", "Variance"], rows,
            Widths: [3, 1.4f, 2, 1.4f, 1.5f, 2]);

        foreach (var p in r.Rows.Where(p => p.Tasks.Count > 0)) // people with no time have only their summary row
        {
            yield return new(["Task", "Status", "Estimate", "Logged", "Actual to date", ""],
                p.Tasks.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", t.Status.Label(), Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.LoggedMinutes),
                     TimeFormat.Minutes(t.TotalMinutes), Marker(t.Status, t.EstimateMinutes, t.IsOver)]).ToList(),
                p.Name, [4.6f, 1.5f, 1.3f, 1.3f, 1.5f, 2.1f]);
        }
    }

    private static IEnumerable<ReportPdfBuilder.Table> EstimateAccuracyTables(EstimateAccuracyReport r)
    {
        var rows = r.Rows.Select(a => (IReadOnlyList<string>)
            [a.Name, a.DoneCount.ToString(), a.Estimate.EstimatedTasks.ToString(), Estimated(a.Estimate),
             Actual(a.Estimate), Variance(a.Estimate), a.Estimate.OverTasks.ToString()]).ToList();
        if (rows.Count > 0)
            rows.Add(["Overall", r.DoneCount.ToString(), r.Overall.EstimatedTasks.ToString(), Estimated(r.Overall),
                Actual(r.Overall), Variance(r.Overall), r.Overall.OverTasks.ToString()]);
        yield return new(["Assignee", "Done", "With estimate", "Estimated", "Actual", "Variance", "Over"], rows,
            Widths: [3, 1, 1.3f, 1.5f, 1.5f, 2, 1]);

        foreach (var a in r.Rows)
        {
            yield return new(["Task", "Estimate", "Actual", "Variance", ""],
                a.Tasks.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.ActualMinutes),
                     t.VarianceMinutes is int v ? TimeFormat.Signed(v) : "-", Marker(TaskItemStatus.Done, t.EstimateMinutes, t.IsOver)]).ToList(),
                a.Name, [4.6f, 1.3f, 1.3f, 1.3f, 2.1f]);
        }
    }

    /// <summary>"12 projects: 8 Active · 1 On Hold · 3 Completed (2 in the period)", by status at the end of the period.</summary>
    public static string StatusCounts(ProjectStatusReport r) =>
        $"{r.Rows.Count} {(r.Rows.Count == 1 ? "project" : "projects")}: " + string.Join(" · ", r.StatusCounts.Select(c =>
            $"{c.Count} {c.Status.Label()}" + (c.ChangedInPeriod > 0 ? $" ({c.ChangedInPeriod} in the period)" : "")));

    /// <summary>"Active → Completed 2026-09-12".</summary>
    public static string Change(ProjectStatusChange c) => $"{c.From.Label()} → {c.To.Label()} {c.At:yyyy-MM-dd}";

    public static string Progress(ProjectTaskCounts t) => t.Total == 0 ? "No tasks" : $"{t.Done}/{t.Total} done";

    public static string Date(DateOnly? d) => d is DateOnly x ? x.ToString("yyyy-MM-dd") : "-";

    /// <summary>The people's estimate reads "-" when none of their tasks has one.</summary>
    public static string Estimated(ProjectPersonRow p) => p.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(p.EstimatedMinutes);

    private static IEnumerable<ReportPdfBuilder.Table> ProjectStatusTables(ProjectStatusReport r)
    {
        static string Open(ProjectTaskCounts t)
        {
            var notes = new List<string>();
            if (t.Overdue > 0) notes.Add($"{t.Overdue} overdue");
            if (t.Blocked > 0) notes.Add($"{t.Blocked} blocked");
            return notes.Count == 0 ? t.Open.ToString() : $"{t.Open}\n{string.Join(", ", notes)}";
        }
        static string Status(ProjectStatusHistory h)
        {
            var lines = new List<string> { h.AtEnd.Label() };
            lines.AddRange(h.Changes.Select(Change));
            if (h.Current != h.AtEnd) lines.Add($"now {h.Current.Label()}");
            return string.Join("\n", lines);
        }
        static string Schedule(ProjectStatusRow p)
        {
            var lines = new List<string> { Date(p.Project.TargetDate) + (p.IsPastTarget ? " (past)" : "") };
            if (p.Schedule is { } s)
                lines.Add($"Buffer {s.BufferStatus.Label()}" + (s.IsStale ? " (out of date)" : ""));
            return string.Join("\n", lines);
        }

        var rows = r.Rows.Select(p => (IReadOnlyList<string>)
            [$"{p.Project.Number} {p.Project.Name}\n{p.Project.DepartmentName} · {p.Project.OwnerName}", Status(p.Status),
             Progress(p.Tasks), Open(p.Tasks), p.Tasks.CreatedInPeriod.ToString(), p.Tasks.DoneInPeriod.ToString(),
             TimeFormat.Minutes(p.LoggedInPeriodMinutes), TimeFormat.Minutes(p.LoggedToDateMinutes), Estimated(p.Estimate),
             Variance(p.Estimate), Schedule(p)]).ToList();
        if (rows.Count > 0)
            rows.Add(["All projects", "", Progress(r.Total.Tasks), Open(r.Total.Tasks), r.Total.Tasks.CreatedInPeriod.ToString(),
                r.Total.Tasks.DoneInPeriod.ToString(), TimeFormat.Minutes(r.Total.LoggedInPeriodMinutes),
                TimeFormat.Minutes(r.Total.LoggedToDateMinutes), Estimated(r.Total.Estimate), Variance(r.Total.Estimate), ""]);
        yield return new(["Project", "Status", "Progress", "Open", "Created", "Done", "Logged", "To date", "Estimated", "Variance", "Target"],
            rows, r.Rows.Count == 0 ? null : StatusCounts(r), [3, 2.4f, 1.5f, 1.4f, 1.3f, 1, 1.3f, 1.3f, 1.5f, 1.9f, 1.6f]);

        foreach (var p in r.Rows.Where(p => p.People.Count > 0 || p.TaskRows.Count > 0))
        {
            var name = $"{p.Project.Number} {p.Project.Name}";
            var people = p.People.Select(x => (IReadOnlyList<string>)
                [x.Name, x.OpenTasks.ToString(), x.DoneInPeriod.ToString(), Estimated(x),
                 TimeFormat.Minutes(x.LoggedInPeriodMinutes), TimeFormat.Minutes(x.LoggedToDateMinutes)]).ToList();
            people.Add(["Total", p.Tasks.Open.ToString(), p.Tasks.DoneInPeriod.ToString(), Estimated(p.Estimate),
                TimeFormat.Minutes(p.LoggedInPeriodMinutes), TimeFormat.Minutes(p.LoggedToDateMinutes)]);
            yield return new(["Person", "Open tasks", "Done in period", "Estimated", "Logged in period", "Logged to date"],
                people, $"{name}: people", [3.4f, 1.2f, 1.4f, 1.4f, 1.5f, 1.5f]);

            yield return new(["Task", "Status", "Assignee", "Due", "Estimate", "Logged in period", "Logged to date", ""],
                p.TaskRows.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", t.Status.Label(), t.Assignee, Date(t.DueDate) + (t.IsOverdue ? " (overdue)" : ""),
                     Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.PeriodMinutes), TimeFormat.Minutes(t.TotalMinutes),
                     Marker(t.Status, t.EstimateMinutes, t.IsOver)]).ToList(),
                $"{name}: tasks", [5, 1.3f, 2, 1.7f, 1.2f, 1.4f, 1.4f, 1.6f]);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        var restricted = ReportingService.RestrictedDepartment(actor);
        CanChooseDepartment = restricted is null;
        if (restricted is Guid r) DepartmentId = r == Guid.Empty ? null : r; // the service enforces it either way
        Definition = ReportCatalog.Get(Kind);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        To ??= today;
        From ??= To.Value.AddDays(-30);
        if (To < From) (From, To) = (To, From);

        var fromUtc = DateTime.SpecifyKind(From.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var filter = new ReportFilter(fromUtc, toUtc, ProjectId, DepartmentId);

        switch (Kind)
        {
            case ReportKind.ClosedByPerson: CountRows = await reporting.ClosedByPersonAsync(filter, ct); break;
            case ReportKind.CreatedByPerson: CountRows = await reporting.CreatedByPersonAsync(filter, ct); break;
            case ReportKind.MeanTimeToRespond: MeanTime = await reporting.MeanTimeToRespondAsync(filter, ct); break;
            case ReportKind.MeanTimeToResolve: MeanTime = await reporting.MeanTimeToResolveAsync(filter, ct); break;
            case ReportKind.TimeByPerson: TimeByPerson = await reporting.TimeByPersonAsync(filter, ct); break;
            case ReportKind.EstimateAccuracy: EstimateAccuracy = await reporting.EstimateAccuracyAsync(filter, ct); break;
            case ReportKind.ProjectStatus: ProjectReport = await reporting.ProjectStatusAsync(filter, ct); break;
        }

        var parts = new List<string> { $"{From:yyyy-MM-dd} to {To:yyyy-MM-dd}" };
        if (DepartmentId is Guid d) parts.Add("Department: " + ((await departments.GetAsync(d, ct)).Name));
        if (ProjectId is Guid p) parts.Add("Project: " + (await projects.GetStatusAsync(p, ct)).Name);
        FilterSummary = string.Join(" · ", parts);
    }
}

using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public enum ReportKind
{
    ClosedByPerson,
    CreatedByPerson,
    MeanTimeToRespond,
    MeanTimeToResolve,
    TimeByPerson,
    EstimateAccuracy,
    ProjectStatus,
    AssetStatus
}

public sealed record ReportFilter(DateTime FromUtc, DateTime ToUtc, Guid? ProjectId, Guid? DepartmentId);

public sealed record PersonCountRow(Guid? UserId, string Name, int Count);

public sealed record PersonAverageRow(Guid? UserId, string Name, int Count, double AverageHours);

public sealed record MeanTimeReport(IReadOnlyList<PersonAverageRow> Rows, int OverallCount, double? OverallAverageHours);

/// <summary>One person's time on one task within a report's date range, summed in SQL (§12 Time by person).</summary>
public sealed record LoggedTime(Guid UserId, Guid TaskId, int Minutes);

/// <summary>What the time reports need to know about a task. <see cref="TotalMinutes"/> is all time logged on it to date, by anyone.</summary>
public sealed record TaskTimeFacts(
    Guid Id, string Number, string Title, TaskItemStatus Status, Guid? AssigneeId, int? EstimateMinutes, int TotalMinutes);

/// <summary>
/// Estimated against actual effort over a set of tasks. Only tasks with an estimate that aren't Cancelled count
/// (as on the project page, §6.10); <see cref="ActualMinutes"/> is all time logged on those tasks to date.
/// </summary>
public sealed record EstimateComparison(int EstimatedTasks, int EstimatedMinutes, int ActualMinutes, int OverTasks)
{
    public static readonly EstimateComparison None = new(0, 0, 0, 0);

    public int VarianceMinutes => ActualMinutes - EstimatedMinutes;

    /// <summary>Variance as a percentage of the estimate; null when nothing is estimated.</summary>
    public double? VariancePercent => EstimatedMinutes == 0 ? null : VarianceMinutes * 100.0 / EstimatedMinutes;
}

/// <summary>
/// Time by person (§12): who logged how much in the range. The totals count each task once, so a task two people
/// worked on shows under both rows but isn't estimated twice in <see cref="Total"/>.
/// </summary>
public sealed record TimeByPersonReport(
    IReadOnlyList<PersonTimeRow> Rows, int TotalLoggedMinutes, int TotalTasks, int TotalUnestimatedTasks, EstimateComparison Total);

public sealed record PersonTimeRow(
    Guid UserId, string Name, int LoggedMinutes, int TaskCount, int UnestimatedTasks, EstimateComparison Estimate,
    IReadOnlyList<PersonTaskTimeRow> Tasks);

/// <summary>A task one person worked on: <see cref="LoggedMinutes"/> is their time in the range, <see cref="TotalMinutes"/> everyone's to date.</summary>
public sealed record PersonTaskTimeRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, int? EstimateMinutes, int LoggedMinutes, int TotalMinutes)
{
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
}

/// <summary>Estimate accuracy (§12): tasks completed in the range, grouped by assignee.</summary>
public sealed record EstimateAccuracyReport(
    IReadOnlyList<AssigneeAccuracyRow> Rows, int DoneCount, EstimateComparison Overall);

public sealed record AssigneeAccuracyRow(
    Guid? UserId, string Name, int DoneCount, EstimateComparison Estimate, IReadOnlyList<TaskEstimateRow> Tasks);

/// <summary>A completed task: its estimate against all time logged on it.</summary>
public sealed record TaskEstimateRow(Guid TaskId, string Number, string Title, int? EstimateMinutes, int ActualMinutes)
{
    public int? VarianceMinutes => EstimateMinutes is int e ? ActualMinutes - e : null;
    public bool IsOver => EstimateMinutes is int e && ActualMinutes > e;
}

/// <summary>What the project status report (§12) needs to know about a project. <see cref="Status"/> is its status now.</summary>
public sealed record ProjectFacts(
    Guid Id, string Number, string Name, ProjectStatus Status, string DepartmentName, string OwnerName,
    DateTime CreatedAt, DateOnly? TargetDate);

/// <summary>One change of a project's status, read from its audit trail.</summary>
public sealed record ProjectStatusChange(Guid ProjectId, DateTime At, ProjectStatus From, ProjectStatus To);

/// <summary>A project's status over a report's period, rebuilt from its status changes (§12 Project status).</summary>
public sealed record ProjectStatusHistory(
    ProjectStatus AtStart, ProjectStatus AtEnd, ProjectStatus Current, IReadOnlyList<ProjectStatusChange> Changes)
{
    /// <summary>Active or On Hold at some point in the period.</summary>
    public bool WasOpen => AtStart.IsOpen() || Changes.Any(c => c.To.IsOpen());
}

/// <summary>A task on a project: <see cref="PeriodMinutes"/> is all time logged on it in the range, <c>Task.TotalMinutes</c> all to date.</summary>
public sealed record ProjectTaskFacts(
    TaskTimeFacts Task, Guid ProjectId, DateOnly? DueDate, DateTime CreatedAt, DateTime? CompletedAt, int PeriodMinutes);

/// <summary>One person's time on one project: in the range, and to date.</summary>
public sealed record ProjectPersonTime(Guid ProjectId, Guid UserId, int PeriodMinutes, int TotalMinutes);

/// <summary>
/// Project status (§12): every project in the range - open at some point in it, or with task or time activity in it -
/// closed ones included. <see cref="StatusCounts"/> counts the projects by their status at the end of the range.
/// </summary>
public sealed record ProjectStatusReport(
    IReadOnlyList<ProjectStatusRow> Rows, ProjectStatusTotals Total, IReadOnlyList<ProjectStatusCount> StatusCounts);

/// <summary>How many projects ended the range in a status, and how many of those changed to it during the range.</summary>
public sealed record ProjectStatusCount(ProjectStatus Status, int Count, int ChangedInPeriod);

/// <summary>The task counts of a project (or all of them): now, apart from the two "in period" counts.</summary>
public sealed record ProjectTaskCounts(
    int Total, int Open, int Blocked, int Overdue, int Done, int Cancelled, int CreatedInPeriod, int DoneInPeriod)
{
    public static readonly ProjectTaskCounts None = new(0, 0, 0, 0, 0, 0, 0, 0);

    public int PercentDone => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);

    public ProjectTaskCounts Plus(ProjectTaskCounts o) => new(Total + o.Total, Open + o.Open, Blocked + o.Blocked,
        Overdue + o.Overdue, Done + o.Done, Cancelled + o.Cancelled, CreatedInPeriod + o.CreatedInPeriod, DoneInPeriod + o.DoneInPeriod);
}

public sealed record ProjectStatusTotals(
    int Projects, ProjectTaskCounts Tasks, int LoggedInPeriodMinutes, int LoggedToDateMinutes, EstimateComparison Estimate);

/// <summary>
/// One project. <see cref="Schedule"/> is its latest critical path analysis (§6.17), only while the project is open;
/// <see cref="IsPastTarget"/> is an open project whose target date has gone by.
/// </summary>
public sealed record ProjectStatusRow(
    ProjectFacts Project, ProjectStatusHistory Status, bool CreatedInPeriod, ProjectTaskCounts Tasks,
    int LoggedInPeriodMinutes, int LoggedToDateMinutes, EstimateComparison Estimate, bool IsPastTarget,
    CriticalPathSummary? Schedule, IReadOnlyList<ProjectPersonRow> People, IReadOnlyList<ProjectTaskRow> TaskRows);

/// <summary>
/// A person on a project: their assigned tasks (open now, done in the range) and the estimates of those that aren't
/// Cancelled, against the time they logged on the project. <see cref="UserId"/> null is the Unassigned row.
/// </summary>
public sealed record ProjectPersonRow(
    Guid? UserId, string Name, int OpenTasks, int DoneInPeriod, int EstimatedTasks, int EstimatedMinutes,
    int LoggedInPeriodMinutes, int LoggedToDateMinutes);

/// <summary>A task in a project's list: <see cref="PeriodMinutes"/> is everyone's time on it in the range, <see cref="TotalMinutes"/> to date.</summary>
public sealed record ProjectTaskRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, string Assignee, DateOnly? DueDate, bool IsOverdue,
    int? EstimateMinutes, int PeriodMinutes, int TotalMinutes, bool CreatedInPeriod, bool DoneInPeriod)
{
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
}

/// <summary>
/// What the asset status report (§12) needs to know about an asset. Its type, location and managing department are as they are
/// now; its last check is the latest on or before the report's as-at day (<see cref="AssetReportRules.AsAt"/>).
/// <see cref="HadTaskActivity"/> is a linked task created, completed or logged against in the range.
/// </summary>
public sealed record AssetFacts(
    Guid Id, string? AssetNumber, string Name, AssetStatus Status, DateOnly? DisposedOn, DateTime CreatedAt,
    Guid DepartmentId, string DepartmentName, Guid TypeId, string TypeName, string? TypeCategory, int? CheckIntervalDays,
    Guid? LocationId, string? LocationName, decimal? PurchaseValue,
    DateOnly? LastCheckedOn, AssetCheckOutcome? LastCheckOutcome, int ChecksInPeriod, bool HadTaskActivity);

/// <summary>One change of an asset's status, read from its audit trail; <see cref="At"/> is when it took effect.</summary>
public sealed record AssetStatusChange(Guid AssetId, DateTime At, AssetStatus From, AssetStatus To);

/// <summary>An asset's status over a report's period, rebuilt from its status changes (§12 Asset status).</summary>
public sealed record AssetStatusHistory(
    AssetStatus AtStart, AssetStatus AtEnd, AssetStatus Current, IReadOnlyList<AssetStatusChange> Changes)
{
    /// <summary>Not Disposed at some point in the period.</summary>
    public bool WasInService => AtStart != AssetStatus.Disposed || Changes.Any(c => c.To != AssetStatus.Disposed);
}

/// <summary>A task about an asset: <see cref="PeriodMinutes"/> is everyone's time on it in the range.</summary>
public sealed record AssetTaskFacts(
    Guid Id, string Number, string Title, TaskItemStatus Status, Guid? AssigneeId, Guid AssetId,
    DateOnly? DueDate, DateTime CreatedAt, DateTime? CompletedAt, int PeriodMinutes);

/// <summary>The linked tasks of some assets: open (and overdue) now; created, done and logged in the range.</summary>
public sealed record AssetTaskCounts(int Open, int Overdue, int Created, int Done, int LoggedMinutes)
{
    public static readonly AssetTaskCounts None = new(0, 0, 0, 0, 0);

    public AssetTaskCounts Plus(AssetTaskCounts o) =>
        new(Open + o.Open, Overdue + o.Overdue, Created + o.Created, Done + o.Done, LoggedMinutes + o.LoggedMinutes);
}

/// <summary>
/// The figures of a group of assets - a type, a location, one within the other, or all of them (§12 Asset status). The statuses
/// are at the end of the range; held means not Disposed then. Values are at cost (<c>PurchaseValue</c>). Checked counts the held
/// assets checked in the range; check overdue and not OK are as at the as-at day.
/// </summary>
public sealed record AssetGroupCounts(
    int Active, int InStorage, int Damaged, int Lost, int Disposed, int Registered,
    decimal HeldValue, int HeldUnvalued, decimal DisposedValue,
    int Checked, int CheckOverdue, int CheckNotOk, AssetTaskCounts Tasks)
{
    public static readonly AssetGroupCounts None = new(0, 0, 0, 0, 0, 0, 0m, 0, 0m, 0, 0, 0, AssetTaskCounts.None);

    public int Total => Active + InStorage + Damaged + Lost + Disposed;
    public int Held => Total - Disposed;

    public int Of(AssetStatus status) => status switch
    {
        AssetStatus.Active => Active,
        AssetStatus.InStorage => InStorage,
        AssetStatus.Damaged => Damaged,
        AssetStatus.Lost => Lost,
        AssetStatus.Disposed => Disposed,
        _ => 0
    };

    public AssetGroupCounts Plus(AssetGroupCounts o) => new(
        Active + o.Active, InStorage + o.InStorage, Damaged + o.Damaged, Lost + o.Lost, Disposed + o.Disposed,
        Registered + o.Registered, HeldValue + o.HeldValue, HeldUnvalued + o.HeldUnvalued, DisposedValue + o.DisposedValue,
        Checked + o.Checked, CheckOverdue + o.CheckOverdue, CheckNotOk + o.CheckNotOk, Tasks.Plus(o.Tasks));
}

/// <summary>
/// A type (with its <see cref="Category"/>) or a location, with its figures and its breakdown by the other: a type's locations, a
/// location's types. <see cref="Id"/> null is "No location".
/// </summary>
public sealed record AssetGroupRow(
    Guid? Id, string Name, string? Category, string DepartmentName, AssetGroupCounts Counts, IReadOnlyList<AssetGroupRow> Breakdown);

/// <summary>How many assets ended the range in a status, and how many of those changed to it during the range.</summary>
public sealed record AssetStatusCount(AssetStatus Status, int Count, int ChangedInPeriod);

/// <summary>An asset with a linked task open now or worked on in the range: its status at the end of the range, and those tasks.</summary>
public sealed record AssetWorkRow(
    Guid AssetId, string? AssetNumber, string Name, string TypeName, string? LocationName, string DepartmentName,
    AssetStatus Status, AssetTaskCounts Tasks, IReadOnlyList<AssetTaskRow> TaskRows);

/// <summary>A task about an asset: <see cref="PeriodMinutes"/> is everyone's time on it in the range.</summary>
public sealed record AssetTaskRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, string Assignee, DateOnly? DueDate, bool IsOverdue,
    DateTime? CompletedAt, int PeriodMinutes, bool CreatedInPeriod, bool DoneInPeriod);

/// <summary>
/// Asset status (§12): every asset in the register during the range, disposed ones included, by type (each with its locations)
/// and by location (each with its types). <see cref="StatusCounts"/> counts the assets by their status at the end of the range;
/// <see cref="Assets"/> are the ones with linked-task work; checks are read as at <see cref="AsAt"/>.
/// </summary>
public sealed record AssetStatusReport(
    IReadOnlyList<AssetGroupRow> ByType, IReadOnlyList<AssetGroupRow> ByLocation, AssetGroupCounts Total,
    IReadOnlyList<AssetStatusCount> StatusCounts, IReadOnlyList<AssetWorkRow> Assets, DateOnly AsAt);

/// <summary>A report in the catalogue. <see cref="ByProject"/> false hides the project filter: assets belong to no project.</summary>
public sealed record ReportDefinition(ReportKind Kind, string Title, string Description, bool ByProject = true);

public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(ReportKind.ClosedByPerson, "Closed count by person",
            "Tasks set to Done in the period, grouped by the assignee who carried them to close."),
        new(ReportKind.CreatedByPerson, "Created count by person",
            "Tasks created in the period, grouped by who authored them. API-created tasks roll up under Claude."),
        new(ReportKind.MeanTimeToRespond, "Mean time to respond",
            "Average time from creation until the task first left Todo, for tasks created in the period."),
        new(ReportKind.MeanTimeToResolve, "Mean time to resolve",
            "Average time from creation to completion, for tasks completed in the period."),
        new(ReportKind.TimeByPerson, "Time by person",
            "Time each person logged in the period, and for the tasks they worked on, each task's estimate against all time logged on it to date."),
        new(ReportKind.EstimateAccuracy, "Estimate accuracy",
            "Tasks completed in the period, grouped by assignee: the estimate against all time logged on them."),
        new(ReportKind.ProjectStatus, "Project status",
            "Every project open or active in the period, closed ones included: its status and status changes, tasks, schedule, and estimated and logged time by person."),
        new(ReportKind.AssetStatus, "Asset status",
            "Every asset in the register during the period, disposed ones included: status by type and location, value at cost, checks, and the work done through linked tasks.",
            ByProject: false)
    ];

    public static ReportDefinition Get(ReportKind kind) => All.First(r => r.Kind == kind);
}

public static class DurationFormat
{
    public static string Hours(double? hours)
    {
        if (hours is null) return "-";
        var h = hours.Value;
        if (h < 1) return $"{Math.Round(h * 60)} min";
        if (h < 48) return $"{h:0.0} h";
        return $"{h / 24:0.0} days";
    }
}

using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public enum ReportKind
{
    ClosedByPerson,
    CreatedByPerson,
    MeanTimeToRespond,
    MeanTimeToResolve,
    TimeByPerson,
    EstimateAccuracy
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

public sealed record ReportDefinition(ReportKind Kind, string Title, string Description);

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
            "Tasks completed in the period, grouped by assignee: the estimate against all time logged on them.")
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

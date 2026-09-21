using System.Text.Json.Serialization;
using Orbit.Application.Scheduling;
using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>Everything one critical path analysis (spec §6.17) is computed from. The engine is a pure function of this.</summary>
public sealed record CriticalPathInput(
    Guid ProjectId,
    DateOnly? TargetDate,
    int? RequiredBufferWorkingDays,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<TaskDependency> Links,
    WorkDayCalendar Calendar,
    DateOnly Today,
    CriticalPathOptions Options);

/// <summary>Codes for plan-readiness findings, so the UI and MCP can group and link them without parsing messages.</summary>
public static class PlanIssueCodes
{
    // Blocking
    public const string CircularDependency = "CircularDependency";
    public const string InvalidReference = "InvalidReference";
    public const string NoScheduledTasks = "NoScheduledTasks";
    // Warnings
    public const string DateConflict = "DateConflict";
    public const string Unscheduled = "Unscheduled";
    public const string NoDependencies = "NoDependencies";
    public const string LinkNotAnalysed = "LinkNotAnalysed";
    public const string NonWorkingDay = "NonWorkingDay";
    public const string HolidayInWindow = "HolidayInWindow";
    public const string WideWindow = "WideWindow";
    public const string ParentWindow = "ParentWindow";
    public const string StandaloneAfterNetwork = "StandaloneAfterNetwork";
    public const string TargetNonWorkingDay = "TargetNonWorkingDay";
    // Informational
    public const string NoNetwork = "NoNetwork";
    public const string NoTargetDate = "NoTargetDate";
    public const string NoRequiredBuffer = "NoRequiredBuffer";
}

/// <summary>One plan-readiness finding: a blocking error, a warning, or a note. Carries the tasks and links it is about.</summary>
public sealed record PlanIssue(
    string Code,
    string Message,
    IReadOnlyList<Guid> TaskIds,
    IReadOnlyList<Guid> LinkIds,
    bool Informational = false);

/// <summary>The schedule and project-buffer block of a result (spec §6.17). Days are working days.</summary>
public sealed class ScheduleSummary
{
    /// <summary>The later of the network completion and the last standalone scheduled task.</summary>
    public DateOnly? PlannedCompletion { get; init; }
    /// <summary>Latest early finish across the dependency network; null when no two scheduled tasks are linked.</summary>
    public DateOnly? NetworkCompletion { get; init; }
    public DateOnly? TargetDate { get; init; }
    /// <summary>Target date minus the required buffer; the day the plan should complete by to keep the buffer intact.</summary>
    public DateOnly? InternalCompletion { get; init; }
    public int? RequiredBufferDays { get; init; }
    public int? BufferConsumedDays { get; init; }
    public int? BufferRemainingDays { get; init; }
    public int? BufferConsumptionPercent { get; init; }
    /// <summary>Working days between planned completion and the internal completion date; positive = the plan finishes early.</summary>
    public int? HeadroomDays { get; init; }
    public int DaysBeyondTarget { get; init; }
    public BufferStatus BufferStatus { get; init; } = BufferStatus.NotAvailable;
    public string? Note { get; init; }
}

/// <summary>One scheduled task's place in the analysis. Floats are working days; null for standalone activities.</summary>
public sealed record TaskAnalysis(
    Guid TaskId,
    string Title,
    TaskItemStatus Status,
    string? Assignee,
    DateOnly? PlannedStart,
    DateOnly? PlannedDue,
    DateOnly EarlyStart,
    DateOnly EarlyFinish,
    DateOnly? LateStart,
    DateOnly? LateFinish,
    int? TotalFloat,
    int? FreeFloat,
    int SpanWorkingDays,
    bool InNetwork,
    bool IsCritical,
    bool IsNearCritical);

/// <summary>A connected driving sequence of critical tasks, first to last.</summary>
public sealed record CriticalPathChain(IReadOnlyList<Guid> TaskIds, IReadOnlyList<string> Titles, DateOnly Start, DateOnly End);

/// <summary>
/// How one driving successor would react if the task finished early (spec §6.17 recovery opportunities).
/// <c>CanPropagate</c>: no other predecessor holds the successor at its current earliest date.
/// <c>DependencyReady</c>: the successor's other workflow gates are already met.
/// </summary>
public sealed record SuccessorReadiness(
    Guid? TaskId,
    string? Title,
    string? Assignee,
    bool CanPropagate,
    bool DependencyReady,
    string DependencyReadiness,
    string ResourceReadiness);

/// <summary>
/// An Early Completion Opportunity (estimate materially smaller than the planning window) and, for a critical task,
/// whether it is also a Critical Path Recovery Opportunity. Informational: nothing is rescheduled.
/// </summary>
public sealed record EarlyCompletionOpportunity(
    Guid TaskId,
    string Title,
    string? Assignee,
    int EstimateMinutes,
    int EstimateWorkingDays,
    int SpanWorkingDays,
    int PotentialDays,
    bool IsCritical,
    bool IsRecoveryOpportunity,
    IReadOnlyList<SuccessorReadiness> Successors,
    string ManagementAction);

/// <summary>A critical predecessor that actually finished before its due date while its driving successor is still to do.</summary>
public sealed record EarlyCompletionNote(Guid TaskId, string Title, DateOnly CompletedOn, DateOnly PlannedDue, Guid SuccessorId, string SuccessorTitle);

public sealed record AnalysisThresholds(int NearCriticalThresholdWorkingDays, int BufferAmberPercent, int BufferRedPercent, int HoursPerWorkingDay);

/// <summary>The full result of one run - what is stored as jsonb and what the pages and MCP present.</summary>
public sealed class CriticalPathResult
{
    public DateTime RunAt { get; init; }
    public Guid ProjectId { get; init; }
    public IReadOnlyList<PlanIssue> Errors { get; init; } = [];
    public IReadOnlyList<PlanIssue> Warnings { get; init; } = [];
    public ScheduleSummary Schedule { get; init; } = new();
    public IReadOnlyList<TaskAnalysis> Tasks { get; init; } = [];
    /// <summary>Links that are tight between two critical tasks - the heavier arrows on the Gantt.</summary>
    public IReadOnlyList<Guid> DrivingLinkIds { get; init; } = [];
    public IReadOnlyList<CriticalPathChain> CriticalPaths { get; init; } = [];
    /// <summary>Paths beyond the cap that were not enumerated.</summary>
    public int CriticalPathsOmitted { get; init; }
    public IReadOnlyList<EarlyCompletionOpportunity> Opportunities { get; init; } = [];
    public IReadOnlyList<EarlyCompletionNote> EarlyCompletions { get; init; } = [];
    public AnalysisThresholds Thresholds { get; init; } = new(5, 33, 66, 8);
    public string InputFingerprint { get; init; } = string.Empty;

    [JsonIgnore] public bool Blocked => Errors.Count > 0;
    [JsonIgnore] public int CriticalTaskCount => Tasks.Count(t => t.IsCritical);
    [JsonIgnore] public int NearCriticalTaskCount => Tasks.Count(t => t.IsNearCritical);
    [JsonIgnore] public int WarningCount => Warnings.Count(w => !w.Informational);
    [JsonIgnore] public IReadOnlySet<Guid> CriticalIds => Tasks.Where(t => t.IsCritical).Select(t => t.TaskId).ToHashSet();
    [JsonIgnore] public IReadOnlySet<Guid> NearCriticalIds => Tasks.Where(t => t.IsNearCritical).Select(t => t.TaskId).ToHashSet();
}

/// <summary>The latest stored analysis of a project, as the pages and MCP read it.</summary>
public sealed record CriticalPathView(CriticalPathAnalysis Analysis, CriticalPathResult Result, bool IsStale);

/// <summary>The headline of the latest analysis, for the project page and get_project_status.</summary>
public sealed record CriticalPathSummary(
    Guid AnalysisId,
    DateTime RunAt,
    bool IsStale,
    DateOnly? PlannedCompletion,
    DateOnly? TargetDate,
    BufferStatus BufferStatus,
    int? BufferRemainingDays,
    int? BufferConsumptionPercent,
    int CriticalTaskCount,
    int NearCriticalTaskCount,
    int WarningCount);

public sealed class WorkingCalendarInput
{
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }
}

public sealed class CalendarExceptionInput
{
    public DateOnly? Date { get; set; }
    public string? Name { get; set; }
    /// <summary>False (the default) for a holiday or shutdown day; true for an exceptional working day.</summary>
    public bool IsWorking { get; set; }
}

public sealed record WorkingCalendarView(WorkingCalendar Calendar, IReadOnlyList<WorkingCalendarException> Exceptions);

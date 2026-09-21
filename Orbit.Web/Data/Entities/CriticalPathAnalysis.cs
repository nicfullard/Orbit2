namespace Orbit.Data.Entities;

/// <summary>
/// One run of critical path analysis on a project (spec §6.17). Rows are appended; the latest by <see cref="RunAt"/>
/// is "the" analysis the project page, the Gantt and MCP show. Not a schedule baseline - it records what the most
/// recent analysis concluded, with the headline numbers as columns and the full per-task result in
/// <see cref="ResultData"/> (jsonb).
/// </summary>
public class CriticalPathAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null for a background run; the synthetic Claude user for an API key.</summary>
    public Guid? RunById { get; set; }
    public ApplicationUser? RunBy { get; set; }

    /// <summary>The day the plan completes: the later of the dependency network's completion and the last standalone scheduled task.</summary>
    public DateOnly? PlannedCompletionDate { get; set; }
    /// <summary>The latest early finish across the dependency network (null when no two scheduled tasks are linked).</summary>
    public DateOnly? NetworkCompletionDate { get; set; }
    public DateOnly? TargetDateAtRun { get; set; }
    public int? RequiredBufferAtRun { get; set; }
    /// <summary>Working days of the required buffer the plan has eaten into (0 when the plan finishes before the internal completion date).</summary>
    public int? BufferConsumedDays { get; set; }
    /// <summary>Required minus consumed; negative when the plan finishes after the target date.</summary>
    public int? BufferRemainingDays { get; set; }
    public int? BufferConsumptionPercent { get; set; }
    public BufferStatus BufferStatus { get; set; } = BufferStatus.NotAvailable;

    public int CriticalTaskCount { get; set; }
    public int NearCriticalTaskCount { get; set; }
    public int CriticalPathCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>SHA-256 of the schedule-driving inputs at the time of the run; a different value now means the analysis is out of date.</summary>
    public string InputFingerprint { get; set; } = string.Empty;
    /// <summary>The full <c>CriticalPathResult</c>, serialised with <c>OrbitJson.Options</c>.</summary>
    public string ResultData { get; set; } = "{}";
}

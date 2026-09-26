using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class TaskFilter
{
    public Guid? ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public TaskItemStatus? Status { get; set; }
    public Guid? AssigneeId { get; set; }
    /// <summary>Only tasks with no assignee - work nobody has picked up yet. Takes precedence over <see cref="AssigneeId"/>.</summary>
    public bool Unassigned { get; set; }
    public TaskPriority? Priority { get; set; }
    public TaskType? Type { get; set; }
    public TaskSource? Source { get; set; }
    public DateOnly? DueBefore { get; set; }
    public DateOnly? DueAfter { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? RecurringTaskDefinitionId { get; set; }
    /// <summary>Only the direct subtasks of this task (§6.15).</summary>
    public Guid? ParentTaskId { get; set; }
    /// <summary>Only tasks about this asset (§6.19).</summary>
    public Guid? AssetId { get; set; }
    /// <summary>Only tasks with no sprint.</summary>
    public bool BacklogOnly { get; set; }
    /// <summary>Exclude Done/Cancelled.</summary>
    public bool OpenOnly { get; set; }
    /// <summary>Only tasks on the day plan for this date (§6.12).</summary>
    public DateOnly? PlannedFor { get; set; }
    /// <summary>Only tasks on today's day plan (UTC date). Ignored when <see cref="PlannedFor"/> is set.</summary>
    public bool PlannedToday { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>Input for creating a task, or the complete new state when updating one.</summary>
public sealed class TaskInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>
    /// The task's department. Null = the project's department when ProjectId is set (or, on update, the task's
    /// current department while it stays on the same project); otherwise the caller's own department.
    /// A caller with tasks.create everywhere may pass a department other than the project's to file a cross-department project task (§6.2.1).
    /// </summary>
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskType Type { get; set; } = TaskType.Task;
    /// <summary>Estimated effort in minutes (§6.10). Null or 0 = no estimate.</summary>
    public int? EstimateMinutes { get; set; }
    public Guid? AssigneeId { get; set; }
    /// <summary>Planned start (§6.15); never after <see cref="DueDate"/>.</summary>
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>The parent task (§6.15): a task on the same project (or a standalone task in the same department). Null = top level.</summary>
    public Guid? ParentTaskId { get; set; }
    /// <summary>
    /// The asset the task is about (§6.19); null = none. A new or changed asset must be one the caller can see that isn't disposed;
    /// on update, the task's current asset is kept without that check.
    /// </summary>
    public Guid? AssetId { get; set; }
    /// <summary>Null on create = Todo; null on update = unchanged.</summary>
    public TaskItemStatus? Status { get; set; }
    /// <summary>Null = backlog.</summary>
    public Guid? SprintId { get; set; }
    /// <summary>Optional client-supplied key so retried API creates don't duplicate.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>One day's plan (spec §6.12): what the team picked to work on that day, and what was left over from last time.</summary>
public sealed class DayPlan
{
    public required DateOnly Date { get; init; }
    /// <summary>Every task with PlannedFor == Date, any status, so "done today" stays visible.</summary>
    public IReadOnlyList<TaskItem> Planned { get; init; } = [];
    /// <summary>The most recent day before Date that still has open planned tasks in scope; null when there are none.</summary>
    public DateOnly? PreviousDate { get; init; }
    /// <summary>Open tasks still sitting on PreviousDate's plan - candidates to carry over.</summary>
    public IReadOnlyList<TaskItem> Unfinished { get; init; } = [];
    public int DoneCount => Planned.Count(t => t.Status == TaskItemStatus.Done);
    public int OpenCount => Planned.Count(t => t.IsOpen);
    public int OverdueCount => Planned.Count(t => t.IsOverdue(Date));
}

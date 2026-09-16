using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class TaskFilter
{
    public Guid? ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public TaskItemStatus? Status { get; set; }
    public Guid? AssigneeId { get; set; }
    public TaskPriority? Priority { get; set; }
    public TaskSource? Source { get; set; }
    public DateOnly? DueBefore { get; set; }
    public DateOnly? DueAfter { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? RecurringTaskDefinitionId { get; set; }
    /// <summary>Only tasks with no sprint.</summary>
    public bool BacklogOnly { get; set; }
    /// <summary>Exclude Done/Cancelled.</summary>
    public bool OpenOnly { get; set; }
    public string? Search { get; set; }
    /// <summary>
    /// Widen a backlog or sprint view to every department. Only honoured for those company-wide
    /// planning views; the normal task list stays scoped to the caller's department.
    /// </summary>
    public bool AllDepartments { get; set; }
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
    /// A System Admin may pass a department other than the project's to file a cross-department project task (§6.2.1).
    /// </summary>
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public Guid? AssigneeId { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Null on create = Todo; null on update = unchanged.</summary>
    public TaskItemStatus? Status { get; set; }
    /// <summary>Null = backlog.</summary>
    public Guid? SprintId { get; set; }
    /// <summary>Optional client-supplied key so retried API creates don't duplicate.</summary>
    public string? IdempotencyKey { get; set; }
}

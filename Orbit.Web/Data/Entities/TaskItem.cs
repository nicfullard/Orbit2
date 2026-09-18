namespace Orbit.Data.Entities;

/// <summary>The spec's "Task" entity (named TaskItem to avoid clashing with System.Threading.Tasks.Task).</summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskType Type { get; set; } = TaskType.Task;
    public Guid? AssigneeId { get; set; }
    public ApplicationUser? Assignee { get; set; }
    public Guid? CreatedById { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
    public TaskSource Source { get; set; } = TaskSource.Manual;
    public DateOnly? DueDate { get; set; }
    /// <summary>
    /// The day this task is on the team's day plan (spec §6.12). Set when someone ticks "Today"; being a date it
    /// simply stops being today rather than needing a reset. Kept on completion so "done today" stays visible.
    /// </summary>
    public DateOnly? PlannedFor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    /// <summary>First time the task left Todo. Drives the mean-time-to-respond report.</summary>
    public DateTime? FirstRespondedAt { get; set; }
    public Guid? RecurringTaskDefinitionId { get; set; }
    public RecurringTaskDefinition? RecurringTaskDefinition { get; set; }
    /// <summary>Null = backlog.</summary>
    public Guid? SprintId { get; set; }
    public Sprint? Sprint { get; set; }
    /// <summary>Client-supplied key so a retried API create doesn't duplicate the task.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>When the "due soon" notification was sent, so it isn't re-sent daily.</summary>
    public DateTime? DueSoonNotifiedAt { get; set; }

    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<TimeEntry> TimeEntries { get; set; } = new List<TimeEntry>();

    public bool IsOpen => !Status.IsClosed();
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate.HasValue && DueDate.Value < today;
}

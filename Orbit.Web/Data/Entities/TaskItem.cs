namespace Orbit.Data.Entities;

/// <summary>The spec's "Task" entity (named TaskItem to avoid clashing with System.Threading.Tasks.Task).</summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    /// <summary>
    /// The asset this task is about (spec §6.19): optional, and building the asset's task history. Set only to an asset the
    /// editor can see that isn't disposed; a link that is kept on a save is never re-checked.
    /// </summary>
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>
    /// The task this one is a subtask of (spec §6.15). Always on the same project as the parent (or both standalone in
    /// the same department); may be in a different department. Never its own ancestor.
    /// </summary>
    public Guid? ParentTaskId { get; set; }
    public TaskItem? ParentTask { get; set; }
    public ICollection<TaskItem> Children { get; set; } = new List<TaskItem>();
    /// <summary>The human-readable id, T-26-00012 (spec §5.1): assigned once at creation, unique, shown wherever the task is named.</summary>
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskType Type { get; set; } = TaskType.Task;
    /// <summary>Estimated effort in minutes (spec §6.10); null = no estimate. Compared with the time logged on the task page.</summary>
    public int? EstimateMinutes { get; set; }
    /// <summary>
    /// Who does the task (spec §6.2.3): any number of people, all equal, or nobody. Loaded with every task (an auto-include,
    /// ApplicationDbContext), since being an assignee is part of what makes a task someone's own (§6.5). Deliberately not
    /// initialised: read it through <see cref="IsAssignedTo"/> and the members beside it, which refuse a task whose assignees
    /// were never loaded rather than answer "nobody".
    /// </summary>
    public ICollection<TaskAssignment> Assignments { get; set; } = null!;
    public Guid? CreatedById { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
    public TaskSource Source { get; set; } = TaskSource.Manual;
    /// <summary>
    /// The requestee (spec §6.2.2): the person the task was created for, beside the assignee who does it and the creator who
    /// typed it. Named on the task form or over MCP within the creator's tasks.create_for reach, or by a request flow's User
    /// question (§6.20). The task counts as the requestee's own (§6.5), so they can open, edit and plan it as its creator can.
    /// Null when the task is for nobody else.
    /// </summary>
    public Guid? RequesteeId { get; set; }
    public ApplicationUser? Requestee { get; set; }
    /// <summary>The planned start (spec §6.15) - the left end of a Gantt bar; never after <see cref="DueDate"/>. The actual start stays <see cref="FirstRespondedAt"/>.</summary>
    public DateOnly? StartDate { get; set; }
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
    /// <summary>Files attached to this task (spec §6.18).</summary>
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    /// <summary>Links where this task is the successor: the tasks this one waits on (spec §6.15).</summary>
    public ICollection<TaskDependency> PredecessorLinks { get; set; } = new List<TaskDependency>();
    /// <summary>Links where this task is the predecessor: the tasks waiting on this one.</summary>
    public ICollection<TaskDependency> SuccessorLinks { get; set; } = new List<TaskDependency>();

    public bool IsOpen => !Status.IsClosed();
    public bool IsOverdue(DateOnly today) => IsOpen && DueDate.HasValue && DueDate.Value < today;

    private ICollection<TaskAssignment> LoadedAssignments =>
        Assignments ?? throw new InvalidOperationException("The task's assignees weren't loaded.");

    /// <summary>Nobody is assigned: work waiting to be taken (§6.5).</summary>
    public bool IsUnassigned => LoadedAssignments.Count == 0;
    public bool IsAssignedTo(Guid userId) => LoadedAssignments.Any(x => x.UserId == userId);
    public IReadOnlyList<Guid> AssigneeIds => LoadedAssignments.Select(x => x.UserId).ToList();
    /// <summary>The assignees, by name.</summary>
    public IReadOnlyList<ApplicationUser> Assignees => LoadedAssignments.Select(x => x.User).OrderBy(u => u.DisplayName).ToList();
    /// <summary>The assignees' names in order, comma-separated; null when nobody is assigned.</summary>
    public string? AssigneeNames => IsUnassigned ? null : string.Join(", ", Assignees.Select(u => u.DisplayName));
}

/// <summary>One person assigned to a task (spec §6.2.3). Earlier assignments are in the task's audit history.</summary>
public class TaskAssignment
{
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Who assigned them; null when a recurring definition did (§6.4).</summary>
    public Guid? AssignedById { get; set; }
    public ApplicationUser? AssignedBy { get; set; }
}

namespace Orbit.Data.Entities;

public class RecurringTaskDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    // Template fields copied onto each generated task
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    /// <summary>
    /// Who each generated task is assigned to (§6.2.3), as far as they are still active when it is generated. Loaded with every
    /// definition and deliberately not initialised, as <see cref="TaskItem.Assignments"/> is.
    /// </summary>
    public ICollection<RecurringTaskAssignment> Assignments { get; set; } = null!;
    /// <summary>The asset each generated task is about (§6.19); left off a generated task once the asset is disposed.</summary>
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>iCal RRULE, e.g. FREQ=WEEKLY;BYDAY=MO</summary>
    public string RecurrenceRule { get; set; } = string.Empty;
    /// <summary>Anchor (DTSTART) for the recurrence rule.</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Due date of the next instance to generate. Null when the series is exhausted.</summary>
    public DateOnly? NextRunDate { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Days before NextRunDate that the task instance is created.</summary>
    public int LeadTimeDays { get; set; }

    public Guid? CreatedById { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastGeneratedAt { get; set; }

    public ICollection<TaskItem> GeneratedTasks { get; set; } = new List<TaskItem>();

    private ICollection<RecurringTaskAssignment> LoadedAssignments =>
        Assignments ?? throw new InvalidOperationException("The recurring definition's assignees weren't loaded.");

    public bool IsAssignedTo(Guid userId) => LoadedAssignments.Any(x => x.UserId == userId);
    public IReadOnlyList<Guid> AssigneeIds => LoadedAssignments.Select(x => x.UserId).ToList();
    /// <summary>The assignees, by name.</summary>
    public IReadOnlyList<ApplicationUser> Assignees => LoadedAssignments.Select(x => x.User).OrderBy(u => u.DisplayName).ToList();
    /// <summary>The assignees' names in order, comma-separated; null when nobody is assigned.</summary>
    public string? AssigneeNames => LoadedAssignments.Count == 0 ? null : string.Join(", ", Assignees.Select(u => u.DisplayName));
}

/// <summary>One person a recurring definition assigns its tasks to (§6.2.3). The definition's audit history has the changes.</summary>
public class RecurringTaskAssignment
{
    public Guid RecurringTaskDefinitionId { get; set; }
    public RecurringTaskDefinition RecurringTaskDefinition { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

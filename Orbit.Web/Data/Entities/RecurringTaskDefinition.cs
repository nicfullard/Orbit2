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
    public Guid? AssigneeId { get; set; }
    public ApplicationUser? Assignee { get; set; }
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
}

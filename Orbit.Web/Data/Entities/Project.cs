namespace Orbit.Data.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    /// <summary>The human-readable id, P-26-00003 (spec §5.1): assigned once at creation, unique, shown wherever the project is named.</summary>
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public Guid OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateOnly? TargetDate { get; set; }
    /// <summary>
    /// Required project buffer (spec §6.17): whole working days of schedule protection to keep immediately before
    /// <see cref="TargetDate"/>. Not allocated to tasks; null = not set.
    /// </summary>
    public int? RequiredBufferWorkingDays { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
    public ICollection<RecurringTaskDefinition> RecurringTaskDefinitions { get; set; } = new List<RecurringTaskDefinition>();
    /// <summary>Files attached to this project (spec §6.18).</summary>
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}

namespace Orbit.Data.Entities;

public class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft archive: an archived department is no longer offered for new users/projects/tasks.</summary>
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
    public ICollection<RecurringTaskDefinition> RecurringTaskDefinitions { get; set; } = new List<RecurringTaskDefinition>();
}

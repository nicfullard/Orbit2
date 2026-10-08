namespace Orbit.Data.Entities;

public class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who runs the department (spec §6.6); optional. They need not belong to it, and it grants them nothing: a request flow can ask them to approve (§6.20).</summary>
    public Guid? ManagerId { get; set; }
    public ApplicationUser? Manager { get; set; }

    /// <summary>Soft archive: an archived department is no longer offered for new users/projects/tasks.</summary>
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
    public ICollection<RecurringTaskDefinition> RecurringTaskDefinitions { get; set; } = new List<RecurringTaskDefinition>();
}

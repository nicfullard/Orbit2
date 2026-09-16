namespace Orbit.Data.Entities;

/// <summary>Company-wide planning window. Deliberately has no ProjectId or DepartmentId.</summary>
public class Sprint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public SprintStatus Status { get; set; } = SprintStatus.Planned;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}

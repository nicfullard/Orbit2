namespace Orbit.Data.Entities;

public class TimeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    /// <summary>The day the work was done.</summary>
    public DateOnly Date { get; set; }
    public int DurationMinutes { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace Orbit.Data.Entities;

public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    /// <summary>API comments point at the synthetic Claude user rather than null.</summary>
    public Guid? AuthorId { get; set; }
    public ApplicationUser? Author { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

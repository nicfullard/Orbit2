namespace Orbit.Data.Entities;

/// <summary>A comment on a task or on an asset (spec §6.19): exactly one of <see cref="TaskId"/> / <see cref="AssetId"/> is set.</summary>
public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>API comments point at the synthetic Claude user rather than null.</summary>
    public Guid? AuthorId { get; set; }
    public ApplicationUser? Author { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

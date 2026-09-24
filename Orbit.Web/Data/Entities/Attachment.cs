namespace Orbit.Data.Entities;

/// <summary>
/// A file attached to a task, a project or an asset (spec §6.18, §6.19). Exactly one of <see cref="TaskId"/> / <see cref="ProjectId"/> /
/// <see cref="AssetId"/> is set. This row is the metadata; the bytes are the companion <see cref="AttachmentContent"/> row, kept in its own
/// table so that listing attachments (or loading a task or project with them) never reads file content - only a download does.
/// </summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>The name the file was uploaded with, reduced to a plain file name.</summary>
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>The bytes. Loaded only when explicitly asked for (download); null on a row from before the bytes moved into the database.</summary>
    public AttachmentContent? Content { get; set; }
    public Guid? UploadedById { get; set; }
    public ApplicationUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

namespace Orbit.Data.Entities;

/// <summary>
/// A file attached to a task or a project (spec §6.18). Exactly one of <see cref="TaskId"/> / <see cref="ProjectId"/> is set.
/// The bytes live outside the database, under the attachments directory, in a file named by <see cref="StoragePath"/> -
/// never by the uploaded name, which is kept here only for display and download.
/// </summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    /// <summary>The name the file was uploaded with, reduced to a plain file name.</summary>
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>Path of the stored bytes relative to the attachments directory (forward slashes).</summary>
    public string StoragePath { get; set; } = string.Empty;
    public Guid? UploadedById { get; set; }
    public ApplicationUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

namespace Orbit.Data.Entities;

/// <summary>
/// The bytes of an <see cref="Attachment"/> (spec §6.18), one row per attachment in a table of its own. Splitting the content
/// from the metadata keeps every query that lists or includes attachments cheap; the content is read only for a download.
/// The row is deleted with its attachment (and so with the task or project) by the database.
/// </summary>
public class AttachmentContent
{
    public Guid AttachmentId { get; set; }
    public Attachment Attachment { get; set; } = null!;
    public byte[] Data { get; set; } = [];
}

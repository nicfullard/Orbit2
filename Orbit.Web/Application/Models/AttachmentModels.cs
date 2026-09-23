namespace Orbit.Application.Models;

/// <summary>One file being uploaded (§6.18). <see cref="Content"/> is read once, to the attachments directory.</summary>
public sealed record AttachmentUpload(string FileName, string? ContentType, long SizeBytes, Stream Content);

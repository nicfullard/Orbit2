using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Attachments;

/// <summary>
/// Serves an attachment's bytes (spec §6.18) to anyone who may see what it is attached to. Always as a download
/// (Content-Disposition: attachment, no content sniffing), so an uploaded HTML or SVG file is never rendered inside Orbit.
/// </summary>
public class DownloadModel(AttachmentService attachments) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var (attachment, content) = await attachments.DownloadAsync(id, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(content, attachment.ContentType, attachment.FileName);
    }
}

using Microsoft.Extensions.Options;

namespace Orbit.Application.Services;

/// <summary>
/// Where attachment bytes live (spec §6.18): a directory tree under <see cref="AttachmentOptions.Path"/>, one file per
/// attachment named by its id - never by the uploaded file name, so nothing a user typed ever becomes a path. Files are
/// grouped by upload month so the directory never grows into one enormous listing.
/// </summary>
public sealed class AttachmentStorage(IOptions<AttachmentOptions> options, IHostEnvironment env)
{
    /// <summary>The absolute attachments directory: <see cref="AttachmentOptions.Path"/>, resolved against the content root when relative.</summary>
    public string Root { get; } = Path.GetFullPath(
        Path.IsPathRooted(options.Value.Path) ? options.Value.Path : Path.Combine(env.ContentRootPath, options.Value.Path));

    /// <summary>Write the content to a new file and return its path relative to <see cref="Root"/>.</summary>
    public async Task<string> SaveAsync(Guid attachmentId, Stream content, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var relative = $"{now:yyyy}/{now:MM}/{attachmentId:N}";
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return relative;
    }

    public Stream Open(string relativePath) =>
        new FileStream(FullPath(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    public void Delete(string relativePath)
    {
        var full = FullPath(relativePath);
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>Resolve a stored path, refusing anything that would escape the attachments directory.</summary>
    private string FullPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment path is outside the attachments directory.");
        return full;
    }
}

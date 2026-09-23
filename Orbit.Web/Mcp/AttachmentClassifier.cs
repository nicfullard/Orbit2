using System.Text;
using System.Text.Unicode;

namespace Orbit.Mcp;

/// <summary>How an attachment's bytes are best handed to a model over MCP (spec §6.18, §7.1).</summary>
public enum AttachmentContentKind
{
    /// <summary>As text: the file is UTF-8 text by its declared type, or by inspection when the type says nothing.</summary>
    Text,
    /// <summary>As an image block: PNG, JPEG, GIF or WebP.</summary>
    Image,
    /// <summary>As base64 in an embedded resource: everything else.</summary>
    Binary
}

/// <summary>
/// Decides how <c>get_attachment</c> returns a file. Text is what a model can read directly, so a file is returned as text
/// when its type says text (text/*, JSON, XML and the like) and the bytes really are UTF-8, or when the type says nothing
/// (octet-stream) and the bytes look like UTF-8 text. A file whose type names a binary format (PDF, Word, zip...) is never
/// sniffed: it is handed over as the file it says it is.
/// </summary>
public static class AttachmentClassifier
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp"
    };

    private static readonly HashSet<string> TextApplicationTypes = new(StringComparer.Ordinal)
    {
        "application/json", "application/xml", "application/javascript", "application/x-javascript", "application/ecmascript",
        "application/yaml", "application/x-yaml", "application/toml", "application/sql", "application/x-sh", "application/x-csh",
        "application/x-ndjson", "application/x-httpd-php"
    };

    /// <summary>Types that say nothing about the content, so the bytes decide.</summary>
    private static readonly HashSet<string> OpaqueTypes = new(StringComparer.Ordinal)
    {
        "", "application/octet-stream", "binary/octet-stream", "application/unknown"
    };

    public static AttachmentContentKind Classify(string? contentType, ReadOnlySpan<byte> data)
    {
        var type = MediaType(contentType);
        if (ImageTypes.Contains(type)) return AttachmentContentKind.Image;
        var declaredText = type.StartsWith("text/", StringComparison.Ordinal)
            || TextApplicationTypes.Contains(type)
            || type.EndsWith("+json", StringComparison.Ordinal)
            || type.EndsWith("+xml", StringComparison.Ordinal);
        if (declaredText || OpaqueTypes.Contains(type))
            return LooksLikeUtf8Text(data) ? AttachmentContentKind.Text : AttachmentContentKind.Binary;
        return AttachmentContentKind.Binary;
    }

    /// <summary>The media type without its parameters, lower-cased: "text/plain; charset=utf-8" becomes "text/plain".</summary>
    public static string MediaType(string? contentType)
    {
        var s = contentType ?? string.Empty;
        var semicolon = s.IndexOf(';');
        if (semicolon >= 0) s = s[..semicolon];
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>Non-empty, valid UTF-8 with no NUL byte - what a text editor would open without complaint.</summary>
    public static bool LooksLikeUtf8Text(ReadOnlySpan<byte> data) =>
        data.Length > 0 && data.IndexOf((byte)0) < 0 && Utf8.IsValid(data);

    /// <summary>Decode UTF-8 text, dropping a leading byte-order mark.</summary>
    public static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
        return Encoding.UTF8.GetString(data);
    }
}

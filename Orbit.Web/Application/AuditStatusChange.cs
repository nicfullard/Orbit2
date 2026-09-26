using System.Text.Json;

namespace Orbit.Application;

/// <summary>
/// Reads a status change from an audit row's <c>Details</c>: <c>{"status":{"from":"Active","to":"Completed"}}</c>, as
/// <see cref="ChangeSet"/> writes it for projects and <c>AssetService</c> writes it for assets. The reports rebuild a status
/// over their period from these (§12).
/// </summary>
public static class AuditStatusChange
{
    /// <summary>False for any other change, for a value that isn't one of <typeparamref name="TStatus"/>'s names, and for bad JSON.</summary>
    public static bool TryRead<TStatus>(string? details, out TStatus from, out TStatus to) where TStatus : struct, Enum
    {
        from = to = default;
        if (string.IsNullOrWhiteSpace(details)) return false;
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
                && status.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String
                && Enum.TryParse(f.GetString(), out from)
                && Enum.TryParse(t.GetString(), out to);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

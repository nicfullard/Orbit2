using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Application;

/// <summary>Shared JSON settings for audit payloads and API responses.</summary>
public static class OrbitJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

/// <summary>Collects field-level changes for an audit entry.</summary>
public sealed class ChangeSet
{
    private readonly Dictionary<string, object?> _changes = new();

    public bool HasChanges => _changes.Count > 0;
    public bool Contains(string field) => _changes.ContainsKey(field);

    public ChangeSet Track<T>(string field, T oldValue, T newValue)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            _changes[field] = new { from = oldValue, to = newValue };
        }
        return this;
    }

    public ChangeSet TrackText(string field, string? oldValue, string? newValue)
    {
        var a = string.IsNullOrWhiteSpace(oldValue) ? null : oldValue.Trim();
        var b = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();
        return Track(field, a, b);
    }

    public IReadOnlyDictionary<string, object?> Changes => _changes;
}

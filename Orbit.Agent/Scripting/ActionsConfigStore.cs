using System.Text.Json;
using Orbit.Scripting;

namespace Orbit.Agent.Scripting;

/// <summary>
/// Reads <c>actions.json</c> from the folder that holds <c>agent.json</c>: the database connections a request action's script may
/// open on this machine (spec §6.20). Orbit never sees them - it sends the script, the agent supplies the connections. Read on every
/// run, so an edit takes effect without a restart. Written by the admin by hand, and not encrypted: keep it readable by the service
/// account only (the agent sets mode 600 on Linux when it first finds the file world-readable; on Windows use the folder's ACL).
/// <code>
/// { "connections": { "erp": { "provider": "sqlserver", "connectionString": "Server=...;Database=...;" } } }
/// </code>
/// </summary>
public static class ActionsConfigStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AgentConfigStore.Path)!, "actions.json");

    public static bool Exists => File.Exists(Path);

    /// <summary>The configured connections by name; empty when there is no file. A file that can't be read throws, with the path in the message.</summary>
    public static IReadOnlyDictionary<string, DbConnectionSpec> LoadConnections()
    {
        if (!Exists) return new Dictionary<string, DbConnectionSpec>(StringComparer.OrdinalIgnoreCase);
        StoredConfig? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(Path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Path} isn't valid JSON: {ex.Message}");
        }
        var connections = new Dictionary<string, DbConnectionSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, spec) in stored?.Connections ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (string.IsNullOrWhiteSpace(spec?.Provider) || string.IsNullOrWhiteSpace(spec.ConnectionString))
                throw new InvalidOperationException($"{Path}: connection \"{name}\" needs a provider (postgres or sqlserver) and a connectionString.");
            connections[name.Trim()] = new DbConnectionSpec(spec.Provider.Trim(), spec.ConnectionString);
        }
        return connections;
    }

    private sealed class StoredConfig
    {
        public Dictionary<string, StoredConnection?>? Connections { get; set; }
    }

    private sealed class StoredConnection
    {
        public string? Provider { get; set; }
        public string? ConnectionString { get; set; }
    }
}

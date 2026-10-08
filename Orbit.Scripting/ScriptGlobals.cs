using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Orbit.Scripting;

/// <summary>
/// What a request action's script sees (spec §6.20): the members of this class are in scope as if they were locals -
/// <c>Request.Number</c>, <c>Inputs["code"]</c>, <c>Values["details.description"]</c>, <c>var db = Connections.Open("erp");</c>,
/// <c>Log("...")</c>. The same class serves on the web server and on an Orbit Agent, so a script compiles the same on both.
/// A script's top level can't hold a <c>using var</c> declaration (it reads as a using directive there); connections are
/// disposed when the run ends, so <c>var db = Connections.Open(...)</c> is enough, or a <c>using (...) { }</c> block.
/// </summary>
public sealed class ScriptGlobals
{
    /// <summary>The request the action runs for.</summary>
    public ScriptRequestInfo Request { get; init; } = new();
    /// <summary>Every token the flow could reference, by its token name without braces: "step.field", "request.number", ...</summary>
    public IReadOnlyDictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>();
    /// <summary>The action's parameters as the step bound them, by parameter key, rendered.</summary>
    public IReadOnlyDictionary<string, string?> Inputs { get; init; } = new Dictionary<string, string?>();
    /// <summary>The named database connections configured where the script runs.</summary>
    public ScriptConnections Connections { get; init; } = new(new Dictionary<string, DbConnectionSpec>());
    public HttpClient Http { get; init; } = null!;
    /// <summary>Appends a line to the step's output, shown on the request page.</summary>
    public Action<string> Log { get; init; } = _ => { };
    /// <summary>Cancelled when the script has run out of time; a long loop or query should pass it on.</summary>
    public CancellationToken Ct { get; init; }

    /// <summary>A token's value, or null when the flow has no such token or it was left blank.</summary>
    public string? Value(string token) => Values.GetValueOrDefault(token);
    /// <summary>A parameter's value, or null when it was left blank.</summary>
    public string? Input(string key) => Inputs.GetValueOrDefault(key);
}

/// <summary>The facts about the request a script may use. Flat strings: nothing here reaches back into Orbit.</summary>
public sealed class ScriptRequestInfo
{
    public string Number { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public ScriptRequester Requester { get; init; } = new();
    /// <summary>The department the request is filed with (the flow's).</summary>
    public string Department { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Flow { get; init; } = string.Empty;
}

public sealed class ScriptRequester
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Email { get; init; }
}

/// <summary>A named connection as configured: <c>postgres</c> or <c>sqlserver</c>, and its connection string.</summary>
public sealed record DbConnectionSpec(string Provider, string ConnectionString);

/// <summary>
/// Opens the named connections a script may use. Every connection handed out is disposed when the run ends, whether or not
/// the script disposed it, so <c>var db = Connections.Open("erp");</c> is enough at a script's top level.
/// </summary>
public sealed class ScriptConnections(IReadOnlyDictionary<string, DbConnectionSpec> specs) : IDisposable
{
    private readonly Dictionary<string, DbConnectionSpec> _specs = new(specs, StringComparer.OrdinalIgnoreCase);
    private readonly List<DbConnection> _opened = [];

    /// <summary>The configured names, for a script that wants to check before it tries.</summary>
    public IReadOnlyCollection<string> Names => _specs.Keys;

    public bool Has(string name) => _specs.ContainsKey(name);

    /// <summary>A connection for the name, not yet opened.</summary>
    public DbConnection Create(string name)
    {
        if (!_specs.TryGetValue(name, out var spec))
            throw new InvalidOperationException(_specs.Count == 0
                ? $"No connection named \"{name}\" is configured here, and no connections are configured at all."
                : $"No connection named \"{name}\" is configured here. Configured: {string.Join(", ", _specs.Keys.Order())}.");
        DbConnection connection = spec.Provider.Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "npgsql" => new NpgsqlConnection(spec.ConnectionString),
            "sqlserver" or "mssql" => new SqlConnection(spec.ConnectionString),
            _ => throw new InvalidOperationException($"Connection \"{name}\" has an unknown provider \"{spec.Provider}\": use postgres or sqlserver.")
        };
        lock (_opened) _opened.Add(connection);
        return connection;
    }

    public DbConnection Open(string name)
    {
        var connection = Create(name);
        connection.Open();
        return connection;
    }

    public async Task<DbConnection> OpenAsync(string name, CancellationToken ct = default)
    {
        var connection = Create(name);
        await connection.OpenAsync(ct);
        return connection;
    }

    public void Dispose()
    {
        List<DbConnection> opened;
        lock (_opened) { opened = [.. _opened]; _opened.Clear(); }
        foreach (var connection in opened)
        {
            try { connection.Dispose(); }
            catch { /* a connection the script already disposed, or one that failed to open */ }
        }
    }
}

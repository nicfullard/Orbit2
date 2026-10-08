using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Orbit.Scripting;

/// <summary>What a run needs besides the script: the request's facts, the values and inputs, and the connections configured here.</summary>
public sealed class ScriptRunContext
{
    public required ScriptRequestInfo Request { get; init; }
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
    public required IReadOnlyDictionary<string, string?> Inputs { get; init; }
    public required IReadOnlyDictionary<string, DbConnectionSpec> Connections { get; init; }
    /// <summary>Null uses the host's shared client.</summary>
    public HttpClient? Http { get; init; }
}

/// <summary>How a run ended. <see cref="Output"/> is what the script logged plus its return value; <see cref="Error"/> why it failed.</summary>
public sealed record ScriptOutcome(bool Ok, string? Output, string? Error, long DurationMs);

/// <summary>
/// Compiles and runs request action scripts (spec §6.20) - C# scripts (Roslyn) with <see cref="ScriptGlobals"/> in scope. One
/// instance per process: a script is compiled once per distinct text and the compiled form kept, since compiling takes longer than
/// most scripts run. Scripts are full-trust code running as this process; nothing here sandboxes them. A script that outruns its
/// time limit is abandoned, not stopped - it only ends if it watches <see cref="ScriptGlobals.Ct"/> - so the limit is a report,
/// not a guard.
/// </summary>
public sealed class ScriptHost : IDisposable
{
    /// <summary>Past this many distinct scripts the cache is emptied: compiled scripts can't be unloaded one at a time.</summary>
    private const int MaxCached = 100;
    /// <summary>What the step keeps of the script's output.</summary>
    public const int MaxOutputChars = 64 * 1024;

    private readonly ConcurrentDictionary<string, Lazy<Script<object>>> _compiled = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>
    /// The references and usings every script gets. Explicit, and the same on the web server and the agent, so a script that
    /// passes the check when it is saved compiles wherever it runs.
    /// </summary>
    public static ScriptOptions Options { get; } = BuildOptions();

    private static ScriptOptions BuildOptions()
    {
        var references = new List<Assembly>
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(DbConnection).Assembly,
            typeof(Npgsql.NpgsqlConnection).Assembly,
            typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly,
            typeof(HttpClient).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(Regex).Assembly,
            typeof(ScriptGlobals).Assembly
        };
        // The facades the compiler wants named when a script touches their types (CS0012); any missing one is simply skipped.
        foreach (var name in new[] { "System.Runtime", "System.Collections", "System.ComponentModel", "System.ComponentModel.Primitives", "System.Threading", "System.Threading.Tasks", "netstandard" })
        {
            try { references.Add(Assembly.Load(name)); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        }
        return ScriptOptions.Default
            .WithReferences(references.Distinct())
            .WithImports("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Text.RegularExpressions",
                "System.Threading", "System.Threading.Tasks", "System.Data", "System.Data.Common", "System.Net.Http", "System.Text.Json",
                "Npgsql", "Microsoft.Data.SqlClient")
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithEmitDebugInformation(false);
    }

    /// <summary>The script's compile errors, each as "line N: message"; empty when it compiles. Warnings are not reported.</summary>
    public IReadOnlyList<string> Check(string code)
    {
        var script = CSharpScript.Create<object>(code, Options, typeof(ScriptGlobals));
        return Errors(script.Compile());
    }

    /// <summary>SHA-256 of the script text, hex: how the cache keys it and how the audit log names a script without quoting it.</summary>
    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public async Task<ScriptOutcome> RunAsync(string code, ScriptRunContext context, TimeSpan timeout, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var log = new ScriptLog();
        Script<object> script;
        try
        {
            script = Compiled(code);
        }
        catch (CompilationErrorException ex)
        {
            return new ScriptOutcome(false, null, "The script doesn't compile: " + string.Join(" ", Errors(ex.Diagnostics)), watch.ElapsedMilliseconds);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var connections = new ScriptConnections(context.Connections);
        var globals = new ScriptGlobals
        {
            Request = context.Request,
            Values = context.Values,
            Inputs = context.Inputs,
            Connections = connections,
            Http = context.Http ?? _http,
            Log = log.Write,
            Ct = cancel.Token
        };

        var run = script.RunAsync(globals, cancel.Token);
        var clock = Task.Delay(timeout, ct);
        if (await Task.WhenAny(run, clock) == clock)
        {
            ct.ThrowIfCancellationRequested();
            cancel.Cancel();
            return new ScriptOutcome(false, log.Text, $"The script did not finish within {timeout.TotalSeconds:0} seconds.", watch.ElapsedMilliseconds);
        }

        ScriptState<object> state;
        try
        {
            state = await run;
        }
        catch (Exception ex)
        {
            return new ScriptOutcome(false, log.Text, Describe(ex), watch.ElapsedMilliseconds);
        }
        var returned = Format(state.ReturnValue);
        if (returned is not null) log.Write(returned);
        return new ScriptOutcome(true, log.Text, null, watch.ElapsedMilliseconds);
    }

    private Script<object> Compiled(string code)
    {
        if (_compiled.Count > MaxCached) _compiled.Clear();
        var key = Hash(code);
        var lazy = _compiled.GetOrAdd(key, _ => new Lazy<Script<object>>(() =>
        {
            var script = CSharpScript.Create<object>(code, Options, typeof(ScriptGlobals));
            var diagnostics = script.Compile();
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new CompilationErrorException("The script doesn't compile.", diagnostics);
            return script;
        }));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A failed compile isn't worth keeping: the text may be fixed and sent again under the same hash only if unchanged.
            _compiled.TryRemove(key, out _);
            throw;
        }
    }

    private static IReadOnlyList<string> Errors(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
            .ToList();

    private static string Describe(Exception ex)
    {
        // The script's own exception, not the wrapper Roslyn may put round it; the type name helps a script author.
        var inner = ex is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : ex;
        return inner is OperationCanceledException ? "The script was cancelled." : $"{inner.GetType().Name}: {inner.Message}";
    }

    private static string? Format(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case IFormattable f: return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            case bool b: return b ? "true" : "false";
        }
        try { return JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }); }
        catch { return value.ToString(); }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The lines a script logs, kept to <see cref="MaxOutputChars"/>; what comes after is dropped with a note.</summary>
    private sealed class ScriptLog
    {
        private readonly StringBuilder _text = new();
        private bool _truncated;

        public void Write(string line)
        {
            lock (_text)
            {
                if (_truncated) return;
                if (_text.Length + line.Length + 1 > MaxOutputChars)
                {
                    _text.AppendLine("[output truncated]");
                    _truncated = true;
                    return;
                }
                _text.AppendLine(line);
            }
        }

        public string? Text
        {
            get { lock (_text) return _text.Length == 0 ? null : _text.ToString().TrimEnd('\r', '\n'); }
        }
    }
}

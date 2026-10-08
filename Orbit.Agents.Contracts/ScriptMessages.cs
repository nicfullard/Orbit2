namespace Orbit.Agents.Contracts;

/// <summary>
/// "Run this request action here" (spec §6.20): the script text and everything it may read travel with the command; the agent's
/// own <c>actions.json</c> supplies the database connections, which never leave the machine. Agent 1.2 and later.
/// </summary>
public sealed class ScriptRunRequest
{
    /// <summary>The C# script as written in Orbit.</summary>
    public string Script { get; set; } = string.Empty;
    /// <summary>The action's parameters, by key, as the step bound and rendered them.</summary>
    public Dictionary<string, string?> Inputs { get; set; } = [];
    /// <summary>Every token the flow could reference, by its name without braces. Form answers included - treat as personal data.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];
    public ScriptRequestFacts Request { get; set; } = new();
    /// <summary>How long the agent lets the script run, so it answers before Orbit stops waiting.</summary>
    public int TimeLimitSeconds { get; set; } = 110;
}

public sealed class ScriptRequestFacts
{
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Guid RequesterId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string? RequesterEmail { get; set; }
    public string Department { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
}

public sealed class ScriptRunResult
{
    /// <summary>Deliberately false as the zero value: a result that fails to deserialize must never read as a success.</summary>
    public bool Ok { get; set; }
    /// <summary>What the script logged and returned.</summary>
    public string? Output { get; set; }
    /// <summary>Why it failed: a compile error, an exception, or the time limit.</summary>
    public string? Error { get; set; }
    public long DurationMs { get; set; }
}

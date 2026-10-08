namespace Orbit.Agents.Contracts;

/// <summary>
/// "Ask this Nagios Core instance for its status" (spec §6.21): the address, the sign-in and the queries travel with the command,
/// so the agent keeps nothing. The queries are query strings for <c>statusjson.cgi</c> only - see <see cref="NagiosCgiRules"/>.
/// A class, not a record: it carries a password, and a generated ToString() would print it. Agent 1.3 and later.
/// </summary>
public sealed class NagiosQueryRequest
{
    /// <summary>The Nagios web address as the admin typed it, e.g. <c>http://nagios.example/nagios/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>Off only when the admin switched it off in Orbit, for an instance behind a self-signed certificate.</summary>
    public bool ValidateCertificate { get; set; } = true;
    /// <summary>Query strings without the question mark, e.g. <c>query=hostlist</c>. Answered in this order.</summary>
    public List<string> Queries { get; set; } = [];
    /// <summary>How long the agent spends on all the queries together, so it answers before Orbit stops waiting.</summary>
    public int TimeLimitSeconds { get; set; } = 30;
}

public sealed class NagiosQueryResult
{
    /// <summary>Deliberately false as the zero value: a result that fails to deserialize must never read as a success.</summary>
    public bool Ok { get; set; }
    /// <summary>Why it failed, in words an admin can act on: the connection, the certificate, the sign-in, or what Nagios answered.</summary>
    public string? Error { get; set; }
    /// <summary>One JSON document per query, in the order asked. Empty unless <see cref="Ok"/>.</summary>
    public List<string> Bodies { get; set; } = [];
    public long DurationMs { get; set; }
}

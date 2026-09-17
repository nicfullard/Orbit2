namespace Orbit.Agents.Contracts;

// Anything here that carries a secret is a class, not a record: a record's generated ToString() prints every
// property, so one stray log statement would write a password to disk.

public sealed class AgentRegistrationRequest
{
    public string Token { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public string? OsDescription { get; set; }
    public string? Version { get; set; }
}

public sealed class AgentRegistrationResponse
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The agent's long-lived credential. Returned once; Orbit keeps only its hash.</summary>
    public string Secret { get; set; } = string.Empty;
}

public sealed class AgentHello
{
    public string? MachineName { get; set; }
    public string? OsDescription { get; set; }
    public string? Version { get; set; }
    public List<string> Capabilities { get; set; } = [];
}

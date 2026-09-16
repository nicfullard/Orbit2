namespace Orbit.Data;

public static class WellKnownIds
{
    /// <summary>The synthetic user that every API key acts as, so CreatedById/AuthorId/audit rows have something to point at.</summary>
    public static readonly Guid ClaudeAgentUserId = new("c1a0de00-0000-4000-8000-000000000001");
    public const string ClaudeAgentEmail = "claude-agent@orbit.local";
    public const string ClaudeAgentDisplayName = "Claude";
}

namespace Orbit.Data;

public static class WellKnownIds
{
    /// <summary>The synthetic user that every API key acts as, so CreatedById/AuthorId/audit rows have something to point at.</summary>
    public static readonly Guid ClaudeAgentUserId = new("c1a0de00-0000-4000-8000-000000000001");
    public const string ClaudeAgentEmail = "claude-agent@orbit.local";
    public const string ClaudeAgentDisplayName = "Claude";

    /// <summary>The single row of directory sign-in settings.</summary>
    public static readonly Guid LdapSettingsId = new("1da90000-0000-4000-8000-000000000001");

    /// <summary>The single row of organisation working-calendar settings (spec §6.17).</summary>
    public static readonly Guid WorkingCalendarId = new("ca1e0000-0000-4000-8000-000000000001");
}

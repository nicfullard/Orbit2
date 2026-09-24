namespace Orbit.Auth;

public static class Policies
{
    /// <summary>The MCP endpoint: authenticated via the API-key scheme only.</summary>
    public const string McpApiKey = "McpApiKey";
    /// <summary>The Orbit Agent hub and endpoints: authenticated via the agent-secret scheme only.</summary>
    public const string Agent = "Agent";

    private const string PermissionPrefix = "permission:";

    /// <summary>
    /// The page-door policy for a permission (spec §6.5): granted at any scope opens the page; the service behind it
    /// applies the scope. One policy per catalogue entry is registered in Program.cs.
    /// </summary>
    public static string Permission(string key) => PermissionPrefix + key;
}

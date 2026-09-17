namespace Orbit.Auth;

public static class Policies
{
    public const string SystemAdmin = "SystemAdmin";
    /// <summary>The MCP endpoint: authenticated via the API-key scheme only.</summary>
    public const string McpApiKey = "McpApiKey";
}

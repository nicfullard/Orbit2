using Orbit.Application.Models;

namespace Orbit.Pages.Admin.Agents;

/// <summary>What the "set up this agent" panel needs: the one-time token and the public URL the agent should dial.</summary>
public sealed record AgentSetupModel(CreatedAgent Created, string BaseUrl)
{
    public static AgentSetupModel For(CreatedAgent created, string? configuredBaseUrl) =>
        new(created, (configuredBaseUrl ?? string.Empty).Trim().TrimEnd('/'));
}

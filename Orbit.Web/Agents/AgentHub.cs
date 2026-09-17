using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Auth;
using Orbit.Data;

namespace Orbit.Agents;

/// <summary>
/// The connection point for on-premises Orbit Agents (spec §8.2). Agents dial out to this hub, so nothing has to
/// be opened on the corporate firewall; Orbit then sends commands down the connection and awaits their results
/// (see <see cref="Application.Services.DirectoryAuthService"/>). The hub itself only tracks who is connected.
/// </summary>
[Authorize(Policy = Policies.Agent)]
public sealed class AgentHub(AgentConnectionRegistry registry, ApplicationDbContext db, ILogger<AgentHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var agentId = AgentId;
        var name = Context.User?.FindFirstValue(ClaimTypes.Name) ?? agentId.ToString();
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        registry.Add(new AgentConnection(agentId, name, Context, ip));

        var now = DateTime.UtcNow;
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LastConnectedAt, now)
            .SetProperty(a => a.LastSeenAt, now)
            .SetProperty(a => a.LastIpAddress, ip), Context.ConnectionAborted);
        logger.LogInformation("Orbit Agent {Agent} ({AgentId}) connected from {Ip}.", name, agentId, ip);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = AgentId;
        // False when the registry already let go of this connection: the agent was revoked, or reconnected and replaced it.
        var wasCurrent = registry.Remove(agentId, Context.ConnectionId);
        logger.LogInformation("Orbit Agent {AgentId} disconnected{Note}.", agentId, wasCurrent ? "" : " (connection had been revoked or replaced)");
        var now = DateTime.UtcNow;
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, now));
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Sent by the agent after every (re)connect. Until it arrives the agent is sent no commands.</summary>
    public async Task Hello(AgentHello hello)
    {
        var agentId = AgentId;
        if (registry.Find(agentId) is { } connection && connection.ConnectionId == Context.ConnectionId)
            connection.Capabilities = new HashSet<string>(hello.Capabilities ?? [], StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.MachineName, Clip(hello.MachineName, 200))
            .SetProperty(a => a.OsDescription, Clip(hello.OsDescription, 200))
            .SetProperty(a => a.Version, Clip(hello.Version, 50))
            .SetProperty(a => a.LastSeenAt, now), Context.ConnectionAborted);
    }

    private Guid AgentId => Guid.TryParse(Context.User?.FindFirstValue(OrbitClaims.AgentId), out var id)
        ? id
        : throw new HubException("Not an Orbit Agent connection.");

    private static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }
}

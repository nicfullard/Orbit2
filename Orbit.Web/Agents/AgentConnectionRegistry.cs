using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Orbit.Agents;

/// <summary>A live hub connection from one Orbit Agent.</summary>
public sealed class AgentConnection(Guid agentId, string agentName, HubCallerContext context, string? remoteIp)
{
    public Guid AgentId { get; } = agentId;
    public string AgentName { get; } = agentName;
    public string ConnectionId => Context.ConnectionId;
    public HubCallerContext Context { get; } = context;
    public string? RemoteIp { get; } = remoteIp;
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    /// <summary>Empty until the agent's Hello arrives; an agent that hasn't said hello is sent no commands.</summary>
    public IReadOnlySet<string> Capabilities { get; set; } = new HashSet<string>();
}

/// <summary>
/// Which agents are connected right now. In-memory, so it assumes a single Orbit instance - the same assumption
/// the in-memory Quartz store already makes. Scaling out would need a SignalR backplane and a shared registry.
/// </summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> _byAgent = new();

    /// <summary>
    /// Registers a connection. One connection per agent: a second one with the same credential replaces the first,
    /// which is what a reconnect looks like while the server hasn't yet noticed the old socket died.
    /// </summary>
    public void Add(AgentConnection connection)
    {
        AgentConnection? previous = null;
        _byAgent.AddOrUpdate(connection.AgentId, connection, (_, old) => { previous = old; return connection; });
        if (previous is not null && previous.ConnectionId != connection.ConnectionId)
            previous.Context.Abort();
    }

    /// <summary>Removes the connection, unless a newer one for the same agent has already replaced it.</summary>
    public bool Remove(Guid agentId, string connectionId) =>
        _byAgent.TryGetValue(agentId, out var current)
        && current.ConnectionId == connectionId
        && _byAgent.TryRemove(new KeyValuePair<Guid, AgentConnection>(agentId, current));

    public AgentConnection? Find(Guid agentId) => _byAgent.GetValueOrDefault(agentId);

    public bool IsOnline(Guid agentId) => _byAgent.ContainsKey(agentId);

    /// <summary>Connected agents able to run the given command, longest-connected (most settled) first.</summary>
    public IReadOnlyList<AgentConnection> WithCapability(string capability) =>
        _byAgent.Values.Where(c => c.Capabilities.Contains(capability)).OrderBy(c => c.ConnectedAt).ToList();

    public int OnlineCount => _byAgent.Count;

    /// <summary>Drops an agent's connection immediately, e.g. when it is revoked.</summary>
    public void Disconnect(Guid agentId)
    {
        if (_byAgent.TryRemove(agentId, out var connection))
            connection.Context.Abort();
    }
}

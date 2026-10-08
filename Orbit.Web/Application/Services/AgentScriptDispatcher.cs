using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Sends a request action's script to an Orbit Agent and waits for the result (spec §6.20, §6.14). An action may name one agent; otherwise
/// the connected agents that announce <see cref="AgentCapabilities.ScriptRun"/> are tried in turn, longest-connected first, so one that
/// dropped its connection doesn't sink the run. The agent's own actions.json supplies the connections; nothing secret travels.
/// </summary>
public sealed class AgentScriptDispatcher(
    IHubContext<AgentHub> hub, AgentConnectionRegistry registry, IOptions<ActionOptions> options, ILogger<AgentScriptDispatcher> logger)
{
    /// <summary>Null when no agent could take it; the result otherwise, successful or not.</summary>
    public async Task<ScriptRunResult?> RunAsync(RequestAction action, ScriptRunRequest request, CancellationToken ct = default)
    {
        var agents = Candidates(action);
        if (agents.Count == 0) return null;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 3600) + 10);
        request.TimeLimitSeconds = Math.Clamp(options.Value.TimeoutSeconds, 5, 3600);
        foreach (var agent in agents)
        {
            var result = await InvokeAsync(agent, request, timeout, ct);
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>Why <see cref="RunAsync"/> found nobody, worded for the request page.</summary>
    public string Unavailable(RequestAction action) =>
        action.AgentId is not null && registry.Find(action.AgentId.Value) is { } c
            ? c.Capabilities.Contains(AgentCapabilities.ScriptRun)
                ? $"Agent \"{c.AgentName}\" did not answer."
                : $"Agent \"{c.AgentName}\" can't run scripts: update it to version 1.2 or later."
            : action.AgentId is not null
                ? "The agent this action names isn't connected."
                : registry.WithCapability(AgentCapabilities.LdapAuthenticate).Count == 0
                    ? "No Orbit Agent is connected, so there is nothing inside the network to run the script."
                    : "No connected Orbit Agent can run scripts: update the agents to version 1.2 or later.";

    private IReadOnlyList<AgentConnection> Candidates(RequestAction action)
    {
        if (action.AgentId is Guid id)
            return registry.Find(id) is { } one && one.Capabilities.Contains(AgentCapabilities.ScriptRun) ? [one] : [];
        return registry.WithCapability(AgentCapabilities.ScriptRun);
    }

    private async Task<ScriptRunResult?> InvokeAsync(AgentConnection agent, ScriptRunRequest request, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            return await hub.Clients.Client(agent.ConnectionId).InvokeAsync<ScriptRunResult>(AgentMethods.RunScript, request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Agent {Agent} did not answer {Method} within {Seconds}s.", agent.AgentName, AgentMethods.RunScript, (int)wait.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Agent {Agent} failed {Method}: {Message}", agent.AgentName, AgentMethods.RunScript, ex.Message);
            return null;
        }
    }
}

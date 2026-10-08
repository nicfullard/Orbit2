using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Nagios;

namespace Orbit.Application.Services;

/// <summary>
/// One reading of a Nagios instance through its agent. <see cref="Snapshot"/> is null when there is nothing to act on;
/// <see cref="NoAgent"/> then says the agent could not be asked at all, as opposed to Nagios answering badly.
/// </summary>
public sealed record NagiosReading(NagiosSnapshot? Snapshot, string? Error, bool NoAgent, long DurationMs);

/// <summary>
/// Asks an Orbit Agent to read a Nagios instance's status and waits for the answer (spec §6.21, §6.14). Unlike a script, a check
/// goes only to the agent the instance names: another agent may sit in a different network, where the same address is a
/// different server - and its answer would look like every problem had gone.
/// </summary>
public sealed class AgentNagiosDispatcher(
    IHubContext<AgentHub> hub, AgentConnectionRegistry registry, IOptions<NagiosOptions> options, ILogger<AgentNagiosDispatcher> logger)
{
    /// <param name="queries"><see cref="NagiosQueries.Check"/> or <see cref="NagiosQueries.Test"/>.</param>
    public async Task<NagiosReading> ReadAsync(Guid? agentId, string? agentName, string baseUrl, string username, string password,
        bool validateCertificate, IReadOnlyList<string> queries, CancellationToken ct = default)
    {
        if (agentId is not Guid id) return new NagiosReading(null, "No Orbit Agent is chosen for this instance.", true, 0);
        var who = string.IsNullOrEmpty(agentName) ? "The agent" : $"Agent \"{agentName}\"";
        if (registry.Find(id) is not { } agent) return new NagiosReading(null, $"{who} isn't connected.", true, 0);
        if (!agent.Capabilities.Contains(AgentCapabilities.NagiosQuery))
            return new NagiosReading(null, $"{who} can't read Nagios: update it to version 1.3 or later.", true, 0);

        var seconds = Math.Clamp(options.Value.QueryTimeoutSeconds, 5, 300);
        var request = new NagiosQueryRequest
        {
            BaseUrl = baseUrl, Username = username, Password = password, ValidateCertificate = validateCertificate,
            Queries = [.. queries], TimeLimitSeconds = seconds
        };
        var result = await InvokeAsync(agent, request, TimeSpan.FromSeconds(seconds + 10), ct);
        if (result is null) return new NagiosReading(null, $"{who} did not answer.", true, 0);
        if (!result.Ok) return new NagiosReading(null, result.Error ?? "The agent could not read Nagios.", false, result.DurationMs);
        if (!NagiosStatusParser.TryParse(result.Bodies, out var snapshot, out var error))
            return new NagiosReading(null, error, false, result.DurationMs);
        if (NagiosStatusParser.Staleness(snapshot, options.Value.StaleAfterSeconds) is { } stale)
            return new NagiosReading(null, stale, false, result.DurationMs);
        return new NagiosReading(snapshot, null, false, result.DurationMs);
    }

    private async Task<NagiosQueryResult?> InvokeAsync(AgentConnection agent, NagiosQueryRequest request, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            return await hub.Clients.Client(agent.ConnectionId).InvokeAsync<NagiosQueryResult>(AgentMethods.QueryNagios, request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Agent {Agent} did not answer {Method} within {Seconds}s.", agent.AgentName, AgentMethods.QueryNagios, (int)wait.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Agent {Agent} failed {Method}: {Message}", agent.AgentName, AgentMethods.QueryNagios, ex.Message);
            return null;
        }
    }
}

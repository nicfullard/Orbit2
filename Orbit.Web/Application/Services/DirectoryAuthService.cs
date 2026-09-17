using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;

namespace Orbit.Application.Services;

/// <summary>
/// Checks a directory (LDAP / Active Directory) password by asking a connected Orbit Agent to do it (spec §8.1).
/// Orbit can't reach the directory itself - it sits behind the corporate firewall - so the request travels down the
/// connection the agent opened to Orbit, and the answer comes back the same way.
/// The password is passed through and never logged or stored.
/// </summary>
public sealed class DirectoryAuthService(
    LdapSettingsService settings,
    AgentConnectionRegistry registry,
    IHubContext<AgentHub> hub,
    IOptions<AgentOptions> options,
    ILogger<DirectoryAuthService> logger)
{
    public async Task<DirectoryAuthOutcome> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        // An empty password is an LDAP "unauthenticated bind", which Active Directory answers with success.
        // It must never reach the directory. (The agent refuses it too.)
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new DirectoryAuthOutcome(DirectoryAuthStatus.InvalidCredentials);

        var ldap = await settings.GetForDispatchAsync(ct);
        if (ldap is null)
        {
            logger.LogWarning("Directory sign-in for {Username} refused: directory sign-in is disabled or not fully configured.", username);
            return new DirectoryAuthOutcome(DirectoryAuthStatus.Unavailable);
        }

        var agents = registry.WithCapability(AgentCapabilities.LdapAuthenticate);
        if (agents.Count == 0)
        {
            logger.LogWarning("Directory sign-in for {Username} unavailable: no Orbit Agent is connected.", username);
            return new DirectoryAuthOutcome(DirectoryAuthStatus.Unavailable);
        }

        var request = new LdapAuthRequest { Settings = ldap, Username = username.Trim(), Password = password };
        foreach (var agent in agents)
        {
            var result = await InvokeAsync<LdapAuthResult>(agent, AgentMethods.Authenticate, request, ct);
            if (result is null) continue;

            switch (result.Status)
            {
                case LdapAuthStatus.Success:
                    logger.LogInformation("Directory sign-in succeeded for {Username} via agent {Agent}.", username, agent.AgentName);
                    return new DirectoryAuthOutcome(DirectoryAuthStatus.Success, result);

                case LdapAuthStatus.InvalidCredentials or LdapAuthStatus.UserNotFound or LdapAuthStatus.Ambiguous:
                    logger.LogWarning("Directory sign-in rejected for {Username} via agent {Agent}: {Status} {Detail}",
                        username, agent.AgentName, result.Status, result.Detail);
                    return new DirectoryAuthOutcome(DirectoryAuthStatus.InvalidCredentials, result);

                default:
                    // This agent couldn't reach the directory; another one might.
                    logger.LogError("Agent {Agent} could not complete a directory sign-in: {Status} {Detail}",
                        agent.AgentName, result.Status, result.Detail);
                    break;
            }
        }
        return new DirectoryAuthOutcome(DirectoryAuthStatus.Unavailable);
    }

    /// <summary>"Test connection" on the Admin &gt; Directory page: bind as the service account and optionally look a user up.</summary>
    public async Task<DirectoryTestOutcome> TestAsync(LdapSettingsInput input, string? sampleUsername, CancellationToken ct = default)
    {
        var ldap = await settings.BuildForTestAsync(input, ct);
        var agent = registry.WithCapability(AgentCapabilities.LdapTest).FirstOrDefault();
        if (agent is null)
            return new DirectoryTestOutcome(null, null, "No Orbit Agent is connected, so there is nothing inside the network to run the test.");

        var request = new LdapTestRequest { Settings = ldap, SampleUsername = string.IsNullOrWhiteSpace(sampleUsername) ? null : sampleUsername.Trim() };
        var result = await InvokeAsync<LdapTestResult>(agent, AgentMethods.TestDirectory, request, ct);
        return result is null
            ? new DirectoryTestOutcome(agent.AgentName, null, $"Agent \"{agent.AgentName}\" did not answer within {TimeoutSeconds} seconds.")
            : new DirectoryTestOutcome(agent.AgentName, result, null);
    }

    private int TimeoutSeconds => Math.Clamp(options.Value.CommandTimeoutSeconds, 1, 120);

    /// <summary>Sends a command to one agent and waits for its result. Null means "no answer": timed out, disconnected or faulted.</summary>
    private async Task<T?> InvokeAsync<T>(AgentConnection agent, string method, object request, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            return await hub.Clients.Client(agent.ConnectionId).InvokeAsync<T>(method, request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Agent {Agent} did not answer {Method} within {Seconds}s.", agent.AgentName, method, TimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Disconnected mid-call, or the agent's handler threw. The message is the agent's, never the request.
            logger.LogError("Agent {Agent} failed {Method}: {Message}", agent.AgentName, method, ex.Message);
            return null;
        }
    }
}

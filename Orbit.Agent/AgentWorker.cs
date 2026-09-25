using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR.Client;
using Orbit.Agent.Ldap;
using Orbit.Agents.Contracts;

namespace Orbit.Agent;

/// <summary>
/// Holds the agent's one connection: outbound to Orbit's hub, kept open, re-established whenever it drops. Orbit
/// sends commands down it and gets each result back as the return value, so the corporate firewall needs no
/// inbound rule at all.
/// </summary>
public sealed class AgentWorker(AgentConfig config, LdapDirectory ldap, ILoggerFactory loggerFactory, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = config.Endpoint(AgentProtocol.HubPath);
        var connectionBuilder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(config.Secret);
                if (!config.UseProxy)
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler http) http.UseProxy = false;
                        return handler;
                    };
                    options.WebSocketConfiguration = ws => ws.Proxy = null;
                }
            })
            .WithAutomaticReconnect(new KeepTryingRetryPolicy(logger));
        connectionBuilder.Services.AddSingleton(loggerFactory);
        await using var connection = connectionBuilder.Build();

        // Commands. Each returns its result to Orbit; a new capability is a new handler here plus its name in Hello.
        connection.On<LdapAuthRequest, LdapAuthResult>(AgentMethods.Authenticate, request => ldap.AuthenticateAsync(request, stoppingToken));
        connection.On<LdapTestRequest, LdapTestResult>(AgentMethods.TestDirectory, request => ldap.TestAsync(request, stoppingToken));
        connection.On<LdapListUsersRequest, LdapListUsersResult>(AgentMethods.ListDirectoryUsers, request => ldap.ListUsersAsync(request, stoppingToken));

        connection.Reconnecting += error =>
        {
            logger.LogWarning("Connection to Orbit lost ({Reason}). Reconnecting...", error?.Message ?? "closed");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            logger.LogInformation("Reconnected to Orbit.");
            await SayHelloAsync(connection, stoppingToken);
        };

        // Closed means automatic reconnect is NOT going to happen. That is what the client gets when Orbit ends the
        // connection on purpose (the agent was revoked, or another process connected with the same credential): the
        // server's close frame forbids reconnecting. Without this the process would sit here connected to nothing.
        TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        logger.LogInformation("Orbit Agent \"{Name}\" ({AgentId}) connecting to {Url}", config.Name, config.AgentId, hubUrl);

        var delay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Automatic reconnect only covers a connection that was established once; starting it is ours to retry.
                await connection.StartAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Couldn't connect to Orbit: {Reason} Retrying in {Seconds}s.", KeepTryingRetryPolicy.Explain(ex), (int)delay.TotalSeconds);
                if (!await WaitAsync(delay, stoppingToken)) break;
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                continue;
            }

            var connectedAt = DateTime.UtcNow;
            logger.LogInformation("Connected to Orbit. Waiting for commands.");
            await SayHelloAsync(connection, stoppingToken);

            var stopped = Task.Delay(Timeout.Infinite, stoppingToken);
            if (await Task.WhenAny(closed.Task, stopped) == stopped) break;

            // Closed again soon after connecting means something keeps throwing this agent off - typically a second
            // copy with the same credential, each knocking the other out. Back off instead of flapping every 2 seconds.
            delay = DateTime.UtcNow - connectedAt > TimeSpan.FromMinutes(1)
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            var reason = (await closed.Task)?.Message;
            logger.LogWarning("Orbit closed the connection{Reason}. If this agent was revoked, or another copy is running with the same configuration, reconnecting will keep failing. Trying again in {Seconds}s.",
                reason is null ? "" : $" ({reason})", (int)delay.TotalSeconds);
            if (!await WaitAsync(delay, stoppingToken)) break;
        }
        logger.LogInformation("Orbit Agent stopping.");
    }

    /// <summary>False when the host is shutting down.</summary>
    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try { await Task.Delay(delay, stoppingToken); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private async Task SayHelloAsync(HubConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.InvokeAsync(AgentMethods.Hello, new AgentHello
            {
                MachineName = Environment.MachineName,
                OsDescription = RuntimeInformation.OSDescription,
                Version = Version,
                Capabilities = [AgentCapabilities.LdapAuthenticate, AgentCapabilities.LdapTest, AgentCapabilities.LdapListUsers]
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without a Hello, Orbit sends this agent no commands. The next reconnect tries again.
            logger.LogError("Couldn't announce this agent to Orbit: {Reason}", ex.Message);
        }
    }

    public static string Version =>
        (typeof(AgentWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Never gives up: an agent that stopped retrying would leave directory users locked out until someone noticed.</summary>
    private sealed class KeepTryingRetryPolicy(ILogger logger) : IRetryPolicy
    {
        private static readonly int[] DelaysSeconds = [0, 2, 5, 10, 30];

        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            var seconds = context.PreviousRetryCount < DelaysSeconds.Length ? DelaysSeconds[context.PreviousRetryCount] : 60;
            if (context.PreviousRetryCount > 0 && context.RetryReason is not null)
                logger.LogWarning("Still can't reach Orbit: {Reason} Next attempt in {Seconds}s.", Explain(context.RetryReason), seconds);
            return TimeSpan.FromSeconds(seconds);
        }

        public static string Explain(Exception ex) => ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            ? "Orbit rejected this agent's credential. It has probably been revoked under Admin > Agents; run 'remove' and 'configure' again with a new token."
            : ex.Message.TrimEnd('.') + ".";
    }
}

using Orbit.Agents.Contracts;
using Orbit.Scripting;

namespace Orbit.Agent.Scripting;

/// <summary>
/// Runs a request action's script on this machine (<see cref="AgentMethods.RunScript"/>): Orbit sends the script and its inputs,
/// the connections come from <see cref="ActionsConfigStore"/>, and the outcome goes back as the command's result. Scripts run as
/// this service's account with whatever it can reach - they were written in Orbit by someone holding actions.create.
/// </summary>
public sealed class ScriptRunner(ScriptHost host, ILogger<ScriptRunner> logger)
{
    public async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, CancellationToken ct)
    {
        IReadOnlyDictionary<string, DbConnectionSpec> connections;
        try
        {
            connections = ActionsConfigStore.LoadConnections();
        }
        catch (Exception ex)
        {
            logger.LogError("Couldn't read the action connections: {Reason}", ex.Message);
            return new ScriptRunResult { Ok = false, Error = $"This agent couldn't read its connections: {ex.Message}" };
        }

        var context = new ScriptRunContext
        {
            Request = new ScriptRequestInfo
            {
                Number = request.Request.Number,
                Title = request.Request.Title,
                Requester = new ScriptRequester { Id = request.Request.RequesterId, Name = request.Request.RequesterName, Email = request.Request.RequesterEmail },
                Department = request.Request.Department,
                Category = request.Request.Category,
                Flow = request.Request.Flow
            },
            Values = request.Values,
            Inputs = request.Inputs,
            Connections = connections
        };
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeLimitSeconds, 5, 3600));
        logger.LogInformation("Running an action for request {Number} (up to {Seconds}s).", request.Request.Number, (int)timeout.TotalSeconds);
        var outcome = await host.RunAsync(request.Script, context, timeout, ct);
        if (outcome.Ok)
            logger.LogInformation("Action for request {Number} finished in {Ms} ms.", request.Request.Number, outcome.DurationMs);
        else
            logger.LogWarning("Action for request {Number} failed after {Ms} ms: {Error}", request.Request.Number, outcome.DurationMs, outcome.Error);
        return new ScriptRunResult { Ok = outcome.Ok, Output = outcome.Output, Error = outcome.Error, DurationMs = outcome.DurationMs };
    }
}

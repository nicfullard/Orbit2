using Orbit.Application.Services;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: runs the request action steps that are ready (spec §6.20) and settles the steps whose task closed while the
/// hook failed. The engine triggers it the moment an action becomes ready; the schedule (Jobs:RequestActions:Cron, every 15 seconds)
/// catches what a restart left behind. One run at a time: a step is claimed before it runs, so two runs never run it twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RequestActionJob(RequestEngine engine, ILogger<RequestActionJob> logger) : IJob
{
    public static readonly JobKey Key = new("request-actions", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var ran = await engine.RunPendingActionsAsync(ct);
            if (ran > 0) logger.LogInformation("Request action job ran {Count} action(s).", ran);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Request action job failed.");
            throw new JobExecutionException(ex);
        }
    }
}

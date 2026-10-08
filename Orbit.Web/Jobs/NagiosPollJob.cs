using Orbit.Application.Services;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: checks the Nagios instances that are due (spec §6.21), each through its Orbit Agent. Scheduled every minute
/// (Jobs:Nagios:Cron); how often an instance is actually read is its own setting. "Check now" on an instance triggers this same
/// job with the instance's id, so a manual check and the scheduled one never run side by side.
/// </summary>
[DisallowConcurrentExecution]
public sealed class NagiosPollJob(NagiosMonitor monitor, ILogger<NagiosPollJob> logger) : IJob
{
    public static readonly JobKey Key = new("nagios-poll", "orbit");
    /// <summary>The job data key carrying the one instance to check now.</summary>
    public const string InstanceId = "instanceId";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            if (context.MergedJobDataMap.TryGetString(InstanceId, out var value) && Guid.TryParse(value, out var instanceId))
                await monitor.CheckAsync(instanceId, ct);
            else
                await monitor.CheckDueAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Nagios job failed.");
            throw new JobExecutionException(ex);
        }
    }
}

using Orbit.Application.Services;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: stops the task clocks whose page has stopped checking in (closed without the page-leave beacon,
/// crashed, asleep), each logged up to its last heartbeat (§6.10, §13 item 64).
/// Scheduled by <see cref="QuartzJobRegistration"/> from Jobs:ClockSweep:Cron.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ClockSweepJob(TimeEntryService time, ILogger<ClockSweepJob> logger) : IJob
{
    public static readonly JobKey Key = new("clock-sweep", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var stopped = await time.StopAllStaleClocksAsync(ct);
            if (stopped > 0) logger.LogInformation("Clock sweep stopped {Count} clock(s) whose page stopped checking in.", stopped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clock sweep failed.");
            // The next fire, a minute later, is the retry.
            throw new JobExecutionException(ex);
        }
    }
}

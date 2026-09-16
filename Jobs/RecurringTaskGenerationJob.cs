using Orbit.Application.Services;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: creates task instances for recurring definitions that fall within their lead time.
/// Scheduled by <see cref="QuartzJobRegistration"/> from Jobs:RecurringTasks:Cron.
/// Quartz resolves the job from a DI scope per execution, so the scoped services are injected directly.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RecurringTaskGenerationJob(RecurrenceService recurrence, ILogger<RecurringTaskGenerationJob> logger) : IJob
{
    public static readonly JobKey Key = new("recurring-task-generation", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var created = await recurrence.GenerateDueTasksAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
            logger.LogInformation("Recurring task generation run complete: {Count} task(s) created. Next fire: {Next}",
                created, context.NextFireTimeUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recurring task generation failed.");
            // RefireImmediately stays false: the next cron fire is the retry.
            throw new JobExecutionException(ex);
        }
    }
}

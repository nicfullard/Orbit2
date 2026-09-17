using Orbit.Application;
using Quartz;

namespace Orbit.Jobs;

/// <summary>Wires Orbit's background jobs into Quartz.NET from the Jobs:* configuration section.</summary>
public static class QuartzJobRegistration
{
    public static IServiceCollection AddOrbitJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(JobOptions.Section).Get<JobOptions>() ?? new JobOptions();

        services.AddQuartz(q =>
        {
            q.UseInMemoryStore();
            Schedule<RecurringTaskGenerationJob>(q, RecurringTaskGenerationJob.Key,
                options.RecurringTasks.Enabled, options.RecurringTasks.Cron, options.RecurringTasks.RunOnStartup,
                startupDelay: TimeSpan.FromSeconds(15));
            Schedule<DueDateNotificationJob>(q, DueDateNotificationJob.Key,
                options.DueDateNotifications.Enabled, options.DueDateNotifications.Cron, options.DueDateNotifications.RunOnStartup,
                startupDelay: TimeSpan.FromSeconds(30));
        });

        // Runs the scheduler as a hosted service; on shutdown, lets in-flight jobs finish.
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return services;
    }

    private static void Schedule<TJob>(IQuartzBuilder q, JobKey key,
        bool enabled, string cron, bool runOnStartup, TimeSpan startupDelay) where TJob : IJob
    {
        if (!enabled) return;
        if (!CronExpression.TryParse(cron, out _))
            throw new InvalidOperationException($"Invalid Quartz cron expression for job {key.Name}: \"{cron}\".");

        q.AddJob<TJob>(j => j.WithIdentity(key).StoreDurably(true));

        // A missed fire (app was down) runs once on the next start instead of being dropped.
        var schedule = CronScheduleBuilder.Create(cron)
            .WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed);
        q.AddTrigger<TJob>(t => t.ForJob(key)
            .WithIdentity($"{key.Name}-cron", key.Group)
            .WithSchedule(schedule));

        if (runOnStartup)
        {
            q.AddTrigger<TJob>(t => t.ForJob(key)
                .WithIdentity($"{key.Name}-startup", key.Group)
                .StartAt(DateTimeOffset.UtcNow.Add(startupDelay)));
        }
    }
}

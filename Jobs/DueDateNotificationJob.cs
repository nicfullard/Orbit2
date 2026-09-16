using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data;
using Orbit.Data.Entities;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: emails assignees whose open tasks are due within the configured lead time.
/// Scheduled by <see cref="QuartzJobRegistration"/> from Jobs:DueDateNotifications:Cron.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DueDateNotificationJob(
    ApplicationDbContext db,
    NotificationService notifications,
    IOptions<JobOptions> options,
    ILogger<DueDateNotificationJob> logger) : IJob
{
    public static readonly JobKey Key = new("due-date-notifications", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var horizon = today.AddDays(Math.Max(0, options.Value.DueDateNotifications.LeadDays));
            var due = await db.Tasks.Include(t => t.Assignee)
                .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled
                    && t.AssigneeId != null && t.DueSoonNotifiedAt == null
                    && t.DueDate != null && t.DueDate >= today && t.DueDate <= horizon)
                .ToListAsync(ct);

            foreach (var task in due)
            {
                await notifications.TaskDueSoonAsync(task, task.Assignee!, today, ct);
                task.DueSoonNotifiedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Due-date notification run complete: {Count} notification(s). Next fire: {Next}",
                due.Count, context.NextFireTimeUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Due-date notification run failed.");
            throw new JobExecutionException(ex);
        }
    }
}

using System.Net;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Task notification call sites, built on Identity's IEmailSender so the same abstraction that
/// sends account emails also sends assignment / due-date emails. The concrete sender is
/// pluggable; the default implementation only logs.
/// </summary>
public sealed class NotificationService(
    IEmailSender emailSender,
    IOptions<AppOptions> app,
    ILogger<NotificationService> logger)
{
    public async Task TaskAssignedAsync(TaskItem task, ApplicationUser assignee, Actor by, CancellationToken ct = default)
    {
        if (!CanNotify(assignee)) return;
        var subject = $"[Orbit] Task assigned to you: {task.Title}";
        var body =
            $"<p>Hi {Enc(assignee.DisplayName)},</p>" +
            $"<p>{Enc(by.DisplayName)} assigned you the task <a href=\"{TaskUrl(task.Id)}\">{Enc(task.Title)}</a>.</p>" +
            $"<p>Priority: {task.Priority}{(task.DueDate is DateOnly d ? $" &middot; Due: {d:yyyy-MM-dd}" : "")}</p>";
        await SendAsync(assignee.Email!, subject, body);
    }

    public async Task TaskDueSoonAsync(TaskItem task, ApplicationUser assignee, DateOnly today, CancellationToken ct = default)
    {
        if (!CanNotify(assignee) || task.DueDate is not DateOnly due) return;
        var days = due.DayNumber - today.DayNumber;
        var when = days <= 0 ? "today" : days == 1 ? "tomorrow" : $"in {days} days";
        var subject = $"[Orbit] Task due {when}: {task.Title}";
        var body =
            $"<p>Hi {Enc(assignee.DisplayName)},</p>" +
            $"<p>The task <a href=\"{TaskUrl(task.Id)}\">{Enc(task.Title)}</a> is due {when} ({due:yyyy-MM-dd}).</p>" +
            $"<p>Status: {task.Status.Label()} &middot; Priority: {task.Priority}</p>";
        await SendAsync(assignee.Email!, subject, body);
    }

    private static bool CanNotify(ApplicationUser user) =>
        user.IsActive && !user.IsSystemAccount && !string.IsNullOrWhiteSpace(user.Email);

    private string TaskUrl(Guid id) => $"{app.Value.BaseUrl.TrimEnd('/')}/Tasks/Details/{id}";

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private async Task SendAsync(string to, string subject, string html)
    {
        try
        {
            await emailSender.SendEmailAsync(to, subject, html);
        }
        catch (Exception ex)
        {
            // Never let a notification failure break the write that triggered it.
            logger.LogWarning(ex, "Failed to send notification email to {Recipient}: {Subject}", to, subject);
        }
    }
}

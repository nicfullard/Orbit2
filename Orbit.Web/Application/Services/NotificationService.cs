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

    /// <summary>
    /// Tells the requestee a task was created for them (§6.2.2) - on the task form, over MCP or through a request flow - or, with
    /// <paramref name="namedLater"/>, that an edit made them the requestee.
    /// </summary>
    public async Task TaskCreatedForAsync(TaskItem task, ApplicationUser requestee, Actor by, bool namedLater = false, CancellationToken ct = default)
    {
        if (!CanNotify(requestee)) return;
        var subject = namedLater ? $"[Orbit] You are the requestee: {task.Title}" : $"[Orbit] Task created for you: {task.Title}";
        var link = $"<a href=\"{TaskUrl(task.Id)}\">{Enc(task.Number)} {Enc(task.Title)}</a>";
        var what = namedLater ? $"named you as the requestee of the task {link}"
            : task.Source == TaskSource.Request ? $"logged the request {link} for you"
            : $"created the task {link} for you";
        var body =
            $"<p>Hi {Enc(requestee.DisplayName)},</p>" +
            $"<p>{Enc(by.DisplayName)} {what}. You can follow and update it as your own.</p>" +
            $"<p>Priority: {task.Priority}{(task.DueDate is DateOnly d ? $" &middot; Due: {d:yyyy-MM-dd}" : "")}</p>";
        await SendAsync(requestee.Email!, subject, body);
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

    /// <summary>A request step is waiting for the person (§6.20): a form to fill in, a page to open, an approval to give.</summary>
    public async Task RequestStepAsync(Request request, ApplicationUser person, string what, CancellationToken ct = default)
    {
        if (!CanNotify(person)) return;
        var subject = $"[Orbit] {request.Number}: please {what}";
        var body =
            $"<p>Hi {Enc(person.DisplayName)},</p>" +
            $"<p>The request <a href=\"{RequestUrl(request.Id)}\">{Enc(request.Number)} {Enc(request.Title)}</a>, logged by {Enc(request.Requester?.DisplayName)}, needs you to {Enc(what)}.</p>";
        await SendAsync(person.Email!, subject, body);
    }

    /// <summary>The request has finished (§6.20) - completed, declined or cancelled - told to the person who logged it.</summary>
    public async Task RequestFinishedAsync(Request request, ApplicationUser requester, CancellationToken ct = default)
    {
        if (!CanNotify(requester)) return;
        var state = request.Status.Label().ToLowerInvariant();
        var subject = $"[Orbit] Request {state}: {request.Title}";
        var body =
            $"<p>Hi {Enc(requester.DisplayName)},</p>" +
            $"<p>Your request <a href=\"{RequestUrl(request.Id)}\">{Enc(request.Number)} {Enc(request.Title)}</a> is {state}.</p>";
        await SendAsync(requester.Email!, subject, body);
    }

    private static bool CanNotify(ApplicationUser user) =>
        user.IsActive && !user.IsSystemAccount && !string.IsNullOrWhiteSpace(user.Email);

    private string TaskUrl(Guid id) => $"{app.Value.BaseUrl.TrimEnd('/')}/Tasks/Details/{id}";

    private string RequestUrl(Guid id) => $"{app.Value.BaseUrl.TrimEnd('/')}/Requests/Request/{id}";

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

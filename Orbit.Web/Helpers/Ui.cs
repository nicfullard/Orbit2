using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>Small presentation helpers shared by the Razor Pages.</summary>
public static class Ui
{
    public static string StatusBadge(TaskItemStatus s) => s switch
    {
        TaskItemStatus.Todo => "text-bg-secondary",
        TaskItemStatus.InProgress => "text-bg-primary",
        TaskItemStatus.Blocked => "text-bg-danger",
        TaskItemStatus.Done => "text-bg-success",
        TaskItemStatus.Cancelled => "text-bg-dark",
        _ => "text-bg-light"
    };

    public static string PriorityBadge(TaskPriority p) => p switch
    {
        TaskPriority.Low => "text-bg-light border",
        TaskPriority.Medium => "text-bg-info",
        TaskPriority.High => "text-bg-warning",
        TaskPriority.Critical => "text-bg-danger",
        _ => "text-bg-light"
    };

    public static string SourceBadge(TaskSource s) => s switch
    {
        TaskSource.Api => "badge-claude",
        TaskSource.Recurring => "text-bg-light border",
        _ => "text-bg-light border"
    };

    public static string SourceLabel(TaskSource s) => s switch
    {
        TaskSource.Api => "Claude",
        TaskSource.Recurring => "Recurring",
        _ => "Manual"
    };

    public static string ProjectStatusBadge(ProjectStatus s) => s switch
    {
        ProjectStatus.Active => "text-bg-success",
        ProjectStatus.OnHold => "text-bg-warning",
        ProjectStatus.Completed => "text-bg-primary",
        ProjectStatus.Archived => "text-bg-secondary",
        _ => "text-bg-light"
    };

    public static string SprintStatusBadge(SprintStatus s) => s switch
    {
        SprintStatus.Planned => "text-bg-secondary",
        SprintStatus.Active => "text-bg-success",
        SprintStatus.Completed => "text-bg-dark",
        _ => "text-bg-light"
    };

    public static string RoleBadge(OrbitRole r) => r switch
    {
        OrbitRole.SystemAdmin => "text-bg-danger",
        OrbitRole.DepartmentAdmin => "text-bg-warning",
        _ => "text-bg-secondary"
    };

    public static string When(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string When(DateTime? utc) => utc is null ? "-" : When(utc.Value);

    public static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "-";

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return When(utc);
    }

    public static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max].TrimEnd() + "...";

    public static string AuthorName(ApplicationUser? user) =>
        user is null ? "Claude" : user.IsSystemAccount ? "Claude" : user.DisplayName;

    public static string ActionLabel(string action) => action switch
    {
        "Created" => "created",
        "Updated" => "updated",
        "StatusChanged" => "changed status of",
        "Completed" => "completed",
        "Archived" => "archived",
        "Unarchived" => "unarchived",
        "SprintChanged" => "re-planned",
        "Started" => "started",
        "CommentAdded" => "commented on",
        "TimeLogged" => "logged time on",
        "TimeUpdated" => "edited a time entry on",
        "TimeDeleted" => "deleted a time entry on",
        "ClockStarted" => "started the clock on",
        "ClockStopped" => "stopped the clock on",
        "Planned" => "planned for the day",
        "Unplanned" => "took off the day plan",
        "Paused" => "paused",
        "Resumed" => "resumed",
        "Generated" => "generated",
        "Deactivated" => "deactivated",
        "Reactivated" => "reactivated",
        "PasswordReset" => "reset the password of",
        "Revoked" => "revoked",
        "Registered" => "registered",
        "Deleted" => "deleted",
        _ => action.ToLowerInvariant()
    };

    public static IEnumerable<SelectListItem> EnumItems<T>(T? selected, string? emptyLabel = null, Func<T, string>? label = null, IEnumerable<T>? only = null)
        where T : struct, Enum
    {
        if (emptyLabel is not null)
            yield return new SelectListItem(emptyLabel, string.Empty, selected is null);
        foreach (var value in only ?? Enum.GetValues<T>())
            yield return new SelectListItem(label?.Invoke(value) ?? value.ToString(), value.ToString(), selected.HasValue && selected.Value.Equals(value));
    }

    public static string TaskStatusLabel(TaskItemStatus s) => s.Label();
    public static string ProjectStatusLabel(ProjectStatus s) => s.Label();
    public static string RoleLabel(OrbitRole r) => r.Label();

    /// <summary>Status options the actor may pick for this task in an inline control.</summary>
    public static IReadOnlyList<TaskItemStatus> AllowedStatuses(Actor actor, TaskItem task) =>
        Enum.GetValues<TaskItemStatus>().Where(s => s == task.Status || AccessPolicy.CanChangeStatus(actor, task, s)).ToList();
}

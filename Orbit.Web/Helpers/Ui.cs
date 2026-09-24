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
        TaskItemStatus.Waiting => "text-bg-info",
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

    public static string TypeBadge(TaskType t) => t switch
    {
        TaskType.Meeting => "bg-primary-subtle text-primary-emphasis border",
        TaskType.Planning => "bg-info-subtle text-info-emphasis border",
        TaskType.Training => "bg-success-subtle text-success-emphasis border",
        TaskType.Audit => "bg-warning-subtle text-warning-emphasis border",
        _ => "text-bg-light border"
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

    public static string BufferStatusBadge(BufferStatus s) => s switch
    {
        BufferStatus.Green => "text-bg-success",
        BufferStatus.Amber => "text-bg-warning",
        BufferStatus.Red => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    public static string AssetStatusBadge(AssetStatus s) => s switch
    {
        AssetStatus.Active => "text-bg-success",
        AssetStatus.InStorage => "text-bg-secondary",
        AssetStatus.Damaged => "text-bg-warning",
        AssetStatus.Lost => "text-bg-danger",
        AssetStatus.Disposed => "text-bg-dark",
        _ => "text-bg-light"
    };

    public static string CheckOutcomeBadge(AssetCheckOutcome o) => o switch
    {
        AssetCheckOutcome.Ok => "text-bg-success",
        AssetCheckOutcome.IssueFound => "text-bg-warning",
        AssetCheckOutcome.NotFound => "text-bg-danger",
        _ => "text-bg-light"
    };

    /// <summary>A purchase value in the organisation's one currency (§6.19): "18,500.00".</summary>
    public static string Money(decimal? value) =>
        value?.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Built-in: danger; any grant at All departments: warning; else secondary (spec §6.5).</summary>
    public static string RoleBadge(RoleRef r) =>
        r.IsBuiltIn ? "text-bg-danger" : r.ReachesEverywhere ? "text-bg-warning" : "text-bg-secondary";

    public static string RoleBadge(ApplicationRole r) =>
        r.IsBuiltIn ? "text-bg-danger"
        : r.Permissions.Any(p => p.Scope == PermissionScope.All) ? "text-bg-warning"
        : "text-bg-secondary";

    public static string ScopeLabel(PermissionScope s) => s.Label();

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

    /// <summary>A file size for people: "812 B", "12.4 KB", "3.1 MB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

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
        "AttachmentAdded" => "attached a file to",
        "AttachmentRemoved" => "removed an attachment from",
        "TimeLogged" => "logged time on",
        "TimeUpdated" => "edited a time entry on",
        "TimeDeleted" => "deleted a time entry on",
        "ClockStarted" => "started the clock on",
        "ClockStopped" => "stopped the clock on",
        "Planned" => "planned for the day",
        "Unplanned" => "took off the day plan",
        "ParentChanged" => "changed the parent of",
        "DependencyAdded" => "added a dependency on",
        "DependencyRemoved" => "removed a dependency on",
        "Paused" => "paused",
        "Resumed" => "resumed",
        "Generated" => "generated",
        "Deactivated" => "deactivated",
        "Reactivated" => "reactivated",
        "PasswordReset" => "reset the password of",
        "Revoked" => "revoked",
        "Unlocked" => "unlocked",
        "Registered" => "registered",
        "Deleted" => "deleted",
        "CriticalPathAnalysed" => "ran a critical path analysis on",
        "ExceptionAdded" => "added a calendar exception to the",
        "ExceptionRemoved" => "removed a calendar exception from the",
        "AssetAssigned" => "assigned",
        "AssetUnassigned" => "unassigned",
        "CheckRecorded" => "recorded a check on",
        "CheckRemoved" => "removed a check from",
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

    /// <summary>Status options the actor may pick for this task in an inline control: every status if they may change it (§6.5), otherwise only the current one.</summary>
    public static IReadOnlyList<TaskItemStatus> AllowedStatuses(Actor actor, TaskItem task) =>
        AccessPolicy.CanChangeStatus(actor, task) ? Enum.GetValues<TaskItemStatus>() : [task.Status];

    /// <summary>Options for the dependency type picker (§6.15): "FS - Finish-to-Start" and so on.</summary>
    public static IEnumerable<SelectListItem> DependencyTypeItems(DependencyType selected) =>
        EnumItems<DependencyType>(selected, null, t => $"{t.Code()} - {t.Label()}");

    /// <summary>A lag in days as "+2 d" / "-1 d", or "-" for none.</summary>
    public static string Lag(int lagDays) => lagDays == 0 ? "-" : $"{(lagDays > 0 ? "+" : "")}{lagDays} d";
}

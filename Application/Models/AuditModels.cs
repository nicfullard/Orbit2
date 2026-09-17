namespace Orbit.Application.Models;

public static class AuditEntity
{
    public const string Task = "Task";
    public const string Project = "Project";
    public const string Sprint = "Sprint";
    public const string RecurringTaskDefinition = "RecurringTaskDefinition";
    public const string Department = "Department";
    public const string User = "User";
    public const string ApiKey = "ApiKey";
}

public static class AuditAction
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string StatusChanged = "StatusChanged";
    public const string Completed = "Completed";
    public const string Archived = "Archived";
    public const string Unarchived = "Unarchived";
    public const string SprintChanged = "SprintChanged";
    public const string Started = "Started";
    public const string CommentAdded = "CommentAdded";
    public const string TimeLogged = "TimeLogged";
    public const string TimeUpdated = "TimeUpdated";
    public const string TimeDeleted = "TimeDeleted";
    public const string ClockStarted = "ClockStarted";
    public const string ClockStopped = "ClockStopped";
    public const string Planned = "Planned";
    public const string Unplanned = "Unplanned";
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
    public const string Generated = "Generated";
    public const string Deactivated = "Deactivated";
    public const string Reactivated = "Reactivated";
    public const string PasswordReset = "PasswordReset";
    public const string Revoked = "Revoked";
}

public sealed class AuditFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? DepartmentId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
}

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
    public const string Agent = "Agent";
    public const string LdapSettings = "LdapSettings";
    public const string WorkingCalendar = "WorkingCalendar";
    /// <summary>A role and its grants (spec §6.5).</summary>
    public const string Role = "Role";
    /// <summary>The asset register (spec §6.19).</summary>
    public const string Asset = "Asset";
    public const string AssetType = "AssetType";
    public const string AssetLocation = "AssetLocation";
    /// <summary>Request flows (spec §6.20): a category, a flow with its steps, an action in the library, and a logged request.</summary>
    public const string RequestCategory = "RequestCategory";
    public const string RequestFlow = "RequestFlow";
    public const string RequestAction = "RequestAction";
    public const string Request = "Request";
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
    public const string AttachmentAdded = "AttachmentAdded";
    public const string AttachmentRemoved = "AttachmentRemoved";
    public const string TimeLogged = "TimeLogged";
    public const string TimeUpdated = "TimeUpdated";
    public const string TimeDeleted = "TimeDeleted";
    public const string ClockStarted = "ClockStarted";
    public const string ClockStopped = "ClockStopped";
    public const string Planned = "Planned";
    public const string Unplanned = "Unplanned";
    /// <summary>Recorded on the child when its parent task changes (§6.15).</summary>
    public const string ParentChanged = "ParentChanged";
    /// <summary>Recorded on both ends of a dependency link (§6.15).</summary>
    public const string DependencyAdded = "DependencyAdded";
    public const string DependencyRemoved = "DependencyRemoved";
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
    public const string Generated = "Generated";
    public const string Deactivated = "Deactivated";
    public const string Reactivated = "Reactivated";
    public const string PasswordReset = "PasswordReset";
    public const string Revoked = "Revoked";
    public const string Unlocked = "Unlocked";
    public const string Registered = "Registered";
    public const string Deleted = "Deleted";
    /// <summary>Recorded on the project each time a critical path analysis is run (§6.17).</summary>
    public const string CriticalPathAnalysed = "CriticalPathAnalysed";
    /// <summary>Working-calendar exceptions (§6.17), recorded against the calendar.</summary>
    public const string ExceptionAdded = "ExceptionAdded";
    public const string ExceptionRemoved = "ExceptionRemoved";
    /// <summary>A person assigned to, or taken off, a task or a recurring definition (§6.2.3): one entry per person.</summary>
    public const string AssigneeAdded = "AssigneeAdded";
    public const string AssigneeRemoved = "AssigneeRemoved";
    /// <summary>A person given or relieved of an asset (§6.19), recorded on the asset.</summary>
    public const string AssetAssigned = "AssetAssigned";
    public const string AssetUnassigned = "AssetUnassigned";
    /// <summary>A check recorded on, or removed from, an asset (§6.19).</summary>
    public const string CheckRecorded = "CheckRecorded";
    public const string CheckRemoved = "CheckRemoved";
    /// <summary>A request's steps (§6.20): an approver's decision, a failed step retried or skipped, the request cancelled.</summary>
    public const string Approved = "Approved";
    public const string Declined = "Declined";
    public const string Retried = "Retried";
    public const string Skipped = "Skipped";
    public const string Cancelled = "Cancelled";
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

namespace Orbit.Data.Entities;

public enum OrbitRole
{
    Member,
    DepartmentAdmin,
    SystemAdmin
}

public enum ProjectStatus
{
    Active,
    OnHold,
    Completed,
    Archived
}

/// <summary>
/// Open: <see cref="Todo"/>, <see cref="InProgress"/>, <see cref="Waiting"/>, <see cref="Blocked"/>. Closed: <see cref="Done"/>, <see cref="Cancelled"/>.
/// <see cref="Waiting"/> is started but paused on something outside the team's control (a reply, a delivery, an approval);
/// <see cref="Blocked"/> is held up by an impediment someone has to remove. Leaving <see cref="Todo"/> for either counts as starting (§6.15).
/// </summary>
public enum TaskItemStatus
{
    Todo,
    InProgress,
    Waiting,
    Blocked,
    Done,
    Cancelled
}

public enum TaskPriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>What kind of work a task is. <see cref="Task"/> is the plain default for ordinary work items.</summary>
public enum TaskType
{
    Meeting,
    Planning,
    Task,
    Training,
    Audit
}

/// <summary>
/// Where a task originated. <see cref="Recurring"/> is an addition to the spec's Manual/Api pair
/// so tasks spawned by the recurring-task job are distinguishable from human- and Claude-created ones.
/// </summary>
public enum TaskSource
{
    Manual,
    Api,
    Recurring
}

public enum SprintStatus
{
    Planned,
    Active,
    Completed
}

/// <summary>
/// How a <see cref="TaskDependency"/> ties its two tasks together (spec §6.15) - the four standard project-management
/// link types. Read as "the successor can't <em>start</em> until the predecessor <em>finishes</em>", and so on.
/// </summary>
public enum DependencyType
{
    FinishToStart,
    StartToStart,
    FinishToFinish,
    StartToFinish
}

/// <summary>
/// How a user proves who they are at sign-in (spec §6.13). <see cref="Local"/> is a password held by Orbit;
/// <see cref="Ldap"/> is checked against the company directory through an Orbit Agent, and Orbit holds no password.
/// </summary>
public enum AuthSource
{
    Local,
    Ldap
}

/// <summary>Lifecycle of an on-premises Orbit Agent (spec §6.14).</summary>
public enum AgentStatus
{
    /// <summary>Created in Orbit, waiting for <c>Orbit.Agent configure</c> to redeem its registration token.</summary>
    Pending,
    Active,
    Revoked
}

/// <summary>Who performed an audited action. <see cref="System"/> covers background jobs.</summary>
public enum ActorType
{
    User,
    Api,
    System
}

/// <summary>The project-buffer attention signal of a critical path analysis (spec §6.17): how much of the required buffer the plan has consumed.</summary>
public enum BufferStatus
{
    /// <summary>No target date, so no buffer can be measured.</summary>
    NotAvailable,
    Green,
    Amber,
    Red
}

public static class Roles
{
    public const string SystemAdmin = nameof(OrbitRole.SystemAdmin);
    public const string DepartmentAdmin = nameof(OrbitRole.DepartmentAdmin);
    public const string Member = nameof(OrbitRole.Member);

    public static readonly string[] All = [SystemAdmin, DepartmentAdmin, Member];
}

public static class TaskStatusExtensions
{
    public static bool IsClosed(this TaskItemStatus status) =>
        status is TaskItemStatus.Done or TaskItemStatus.Cancelled;

    public static string Label(this TaskItemStatus status) => status switch
    {
        TaskItemStatus.InProgress => "In Progress",
        _ => status.ToString()
    };

    public static string Label(this ProjectStatus status) => status switch
    {
        ProjectStatus.OnHold => "On Hold",
        _ => status.ToString()
    };

    public static string Label(this OrbitRole role) => role switch
    {
        OrbitRole.SystemAdmin => "System Admin",
        OrbitRole.DepartmentAdmin => "Department Admin",
        _ => role.ToString()
    };

    public static string Label(this AuthSource source) => source switch
    {
        AuthSource.Ldap => "Directory (LDAP)",
        _ => "Local password"
    };

    public static string Label(this DependencyType type) => type switch
    {
        DependencyType.FinishToStart => "Finish-to-Start",
        DependencyType.StartToStart => "Start-to-Start",
        DependencyType.FinishToFinish => "Finish-to-Finish",
        DependencyType.StartToFinish => "Start-to-Finish",
        _ => type.ToString()
    };

    public static string Label(this BufferStatus status) => status switch
    {
        BufferStatus.NotAvailable => "Not available",
        _ => status.ToString()
    };

    /// <summary>The conventional two-letter code: FS, SS, FF, SF.</summary>
    public static string Code(this DependencyType type) => type switch
    {
        DependencyType.FinishToStart => "FS",
        DependencyType.StartToStart => "SS",
        DependencyType.FinishToFinish => "FF",
        DependencyType.StartToFinish => "SF",
        _ => type.ToString()
    };

    /// <summary>FS and SS gate the successor's <em>start</em> (leaving Todo); FF and SF gate its <em>finish</em> (Done).</summary>
    public static bool GatesStart(this DependencyType type) =>
        type is DependencyType.FinishToStart or DependencyType.StartToStart;

    /// <summary>Whether the link waits for the predecessor to <em>finish</em> (FS, FF) rather than merely to <em>start</em> (SS, SF).</summary>
    public static bool WaitsForFinish(this DependencyType type) =>
        type is DependencyType.FinishToStart or DependencyType.FinishToFinish;
}

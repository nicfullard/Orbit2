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

public enum TaskItemStatus
{
    Todo,
    InProgress,
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
}

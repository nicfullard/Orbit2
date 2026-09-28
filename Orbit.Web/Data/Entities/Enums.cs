namespace Orbit.Data.Entities;

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

/// <summary>
/// What kind of work a task is. <see cref="Task"/> is the plain default for ordinary work items. A <see cref="Change"/> alters
/// something that already exists (a process, setting, system or access); a <see cref="Request"/> asks for something new. The
/// <see cref="Request"/> type is not <see cref="TaskSource.Request"/>: the type is what kind of work it is, the source where it came from.
/// </summary>
public enum TaskType
{
    Meeting,
    Planning,
    Task,
    Training,
    Audit,
    Change,
    Request
}

/// <summary>
/// Where a task originated. <see cref="Recurring"/> is an addition to the spec's Manual/Api pair
/// so tasks spawned by the recurring-task job are distinguishable from human- and Claude-created ones.
/// <see cref="Request"/> is a task logged through a department's request flow (§6.20).
/// </summary>
public enum TaskSource
{
    Manual,
    Api,
    Recurring,
    Request
}

/// <summary>What choosing a request option does (§6.20): walk through its questions and log a task, or open a web page.</summary>
public enum RequestOptionKind
{
    Flow,
    Link
}

/// <summary>The kind of answer a request question takes (§6.20).</summary>
public enum RequestQuestionType
{
    Text,
    Number,
    Date,
    /// <summary>One of the assets the person holds, or a description of something else. A picked asset becomes the task's asset.</summary>
    Asset,
    /// <summary>How urgent the request is, one of the four task priorities; it becomes the task's priority.</summary>
    Urgency,
    Choice,
    /// <summary>
    /// Who the request is for: a person the requester may log for (requests.submit scope). It becomes the task's RequestedForId. At
    /// Own scope it isn't asked - the answer is the requester.
    /// </summary>
    User
}

/// <summary>A request category's accent colour (§6.20); each has a light and a dark value in site.css.</summary>
public enum RequestColour
{
    Blue,
    Red,
    Orange,
    Green,
    Purple,
    Teal,
    Grey
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

/// <summary>
/// An asset's state (spec §6.19). <see cref="Disposed"/> is the end state: nobody holds it and no check is due.
/// <see cref="Lost"/> keeps its assignments, since who last had it is the useful fact.
/// </summary>
public enum AssetStatus
{
    Active,
    InStorage,
    Damaged,
    Lost,
    Disposed
}

/// <summary>What a check found (§6.19). A check never changes the asset; a last outcome other than Ok is flagged instead.</summary>
public enum AssetCheckOutcome
{
    Ok,
    IssueFound,
    NotFound
}

/// <summary>The kind of value an asset type's property holds (§6.19).</summary>
public enum AssetPropertyType
{
    Text,
    Number,
    Date,
    YesNo,
    Choice
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

    /// <summary>Active and On Hold projects are still running; Completed and Archived ones are closed.</summary>
    public static bool IsOpen(this ProjectStatus status) => status is ProjectStatus.Active or ProjectStatus.OnHold;

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

    public static string Label(this AssetStatus status) => status switch
    {
        AssetStatus.InStorage => "In storage",
        _ => status.ToString()
    };

    public static string Label(this AssetCheckOutcome outcome) => outcome switch
    {
        AssetCheckOutcome.Ok => "OK",
        AssetCheckOutcome.IssueFound => "Issue found",
        AssetCheckOutcome.NotFound => "Not found",
        _ => outcome.ToString()
    };

    public static string Label(this AssetPropertyType type) => type switch
    {
        AssetPropertyType.YesNo => "Yes / No",
        _ => type.ToString()
    };

    public static string Label(this RequestQuestionType type) => type switch
    {
        RequestQuestionType.Text => "Text",
        RequestQuestionType.Number => "Number",
        RequestQuestionType.Date => "Date",
        RequestQuestionType.Asset => "Asset",
        RequestQuestionType.Urgency => "Urgency",
        RequestQuestionType.Choice => "Choice (pick one)",
        RequestQuestionType.User => "User (who it's for)",
        _ => type.ToString()
    };

    public static string Label(this RequestOptionKind kind) => kind switch
    {
        RequestOptionKind.Flow => "Questions, then log a task",
        RequestOptionKind.Link => "Open a web page",
        _ => kind.ToString()
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

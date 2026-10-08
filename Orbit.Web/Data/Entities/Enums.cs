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

/// <summary>What a step of a request flow is (§6.20). A flow may hold any number of each.</summary>
public enum RequestStepKind
{
    /// <summary>Questions answered by the requester (or a named person): the flow's fields.</summary>
    Form,
    /// <summary>Ordered stages of approvers; ends Accepted or Declined, and later steps may hang on either.</summary>
    Approval,
    /// <summary>Creates an Orbit task from earlier answers; done when the task is.</summary>
    Task,
    /// <summary>Runs a script from the action library, on the web server or an Orbit Agent.</summary>
    Action,
    /// <summary>Opens a web page in a new tab; done when the person marks it so.</summary>
    Url
}

/// <summary>The kind of answer a form field takes (§6.20).</summary>
public enum RequestFieldType
{
    Text,
    Number,
    Date,
    Choice,
    /// <summary>How urgent it is: one of the four task priorities, shown with what each means. A task step can take its priority from it.</summary>
    Urgency,
    /// <summary>One or more files, kept on the request and copied to a task step that asks for them.</summary>
    Attachment,
    /// <summary>An asset from the register, within the field's scope.</summary>
    Asset,
    /// <summary>One of the active asset types within the field's scope; its name is the answer.</summary>
    AssetType,
    /// <summary>An open project, within the field's scope.</summary>
    Project,
    /// <summary>An active person, within the field's scope.</summary>
    User
}

/// <summary>
/// What an Asset, Asset type, Project or User field offers (§6.20): the flow's department's own, the whole company's, or - assets
/// only - the ones the person asking holds.
/// </summary>
public enum RequestPickerScope
{
    Department,
    Company,
    Held
}

/// <summary>How an approval step ended, and what a dependency on one may require before the dependent step starts.</summary>
public enum RequestOutcome
{
    Accepted,
    Declined
}

/// <summary>How many of a stage's approvers must approve: one of them, or all of them. Any decline declines the stage.</summary>
public enum ApprovalRule
{
    Any,
    All
}

/// <summary>Who an approver is (§6.20): a named person, everyone with a role in a department, or that role in the requester's department.</summary>
public enum ApproverKind
{
    Person,
    RoleInDepartment,
    RoleInRequestersDepartment
}

/// <summary>Whom a task step's task is for (its requestee, §6.2.2): nobody, the requester, or the person a User field names.</summary>
public enum RequesteeSource
{
    None,
    Requester,
    Field
}

/// <summary>Where a request action's script runs (§6.20): in the Orbit web process, or on an Orbit Agent inside the network.</summary>
public enum ActionRunsOn
{
    Web,
    Agent
}

/// <summary>A request's state (§6.20). <see cref="InProgress"/> until every step has settled.</summary>
public enum RequestStatus
{
    InProgress,
    Completed,
    Declined,
    Cancelled
}

/// <summary>
/// A request step's state (§6.20). <see cref="Pending"/> waits on its dependencies; <see cref="Ready"/> is being done; the rest are
/// settled - except <see cref="Failed"/>, which someone retries or skips.
/// </summary>
public enum RequestStepStatus
{
    Pending,
    Ready,
    Completed,
    Declined,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>One approver's answer in an approval stage.</summary>
public enum ApprovalDecision
{
    Pending,
    Approved,
    Declined
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

    public static string Label(this RequestStepKind kind) => kind switch
    {
        RequestStepKind.Url => "Web page",
        _ => kind.ToString()
    };

    public static string Label(this RequestFieldType type) => type switch
    {
        RequestFieldType.Choice => "Choice (pick one)",
        RequestFieldType.Attachment => "Files",
        RequestFieldType.AssetType => "Asset type",
        RequestFieldType.User => "Person",
        _ => type.ToString()
    };

    public static string Label(this RequestPickerScope scope) => scope switch
    {
        RequestPickerScope.Department => "The flow's department's",
        RequestPickerScope.Company => "The whole company's",
        RequestPickerScope.Held => "The ones the person asking holds",
        _ => scope.ToString()
    };

    public static string Label(this ApprovalRule rule) => rule switch
    {
        ApprovalRule.Any => "Any one of them",
        ApprovalRule.All => "All of them",
        _ => rule.ToString()
    };

    public static string Label(this ApproverKind kind) => kind switch
    {
        ApproverKind.Person => "A person",
        ApproverKind.RoleInDepartment => "Everyone with a role in a department",
        ApproverKind.RoleInRequestersDepartment => "Everyone with a role in the requester's department",
        _ => kind.ToString()
    };

    public static string Label(this RequesteeSource source) => source switch
    {
        RequesteeSource.None => "Nobody",
        RequesteeSource.Requester => "The person who logged the request",
        RequesteeSource.Field => "The person a field names",
        _ => source.ToString()
    };

    public static string Label(this ActionRunsOn runsOn) => runsOn switch
    {
        ActionRunsOn.Web => "On the Orbit server",
        ActionRunsOn.Agent => "On an Orbit Agent",
        _ => runsOn.ToString()
    };

    public static string Label(this RequestStatus status) => status switch
    {
        RequestStatus.InProgress => "In progress",
        _ => status.ToString()
    };

    public static string Label(this RequestStepStatus status) => status switch
    {
        RequestStepStatus.Pending => "Waiting",
        RequestStepStatus.Ready => "In progress",
        _ => status.ToString()
    };

    public static string Label(this ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Pending => "Not yet decided",
        _ => decision.ToString()
    };

    /// <summary>Settled: nothing more will happen to the step. Failed isn't, since it is retried or skipped.</summary>
    public static bool IsSettled(this RequestStepStatus status) =>
        status is RequestStepStatus.Completed or RequestStepStatus.Declined or RequestStepStatus.Skipped or RequestStepStatus.Cancelled;

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

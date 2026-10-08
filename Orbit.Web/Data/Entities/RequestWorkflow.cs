namespace Orbit.Data.Entities;

/// <summary>
/// One thing people can ask for under a category (spec §6.20): an ordered set of steps - forms, approvals, tasks, actions and web
/// pages - with dependencies between them. Logging it creates a <see cref="Request"/> that walks the steps. Requests reference the
/// flow and its steps, so a flow with requests in progress is archived rather than deleted, and its structure is frozen while they run.
/// </summary>
public class RequestFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public RequestCategory Category { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestFlowStep> Steps { get; set; } = new List<RequestFlowStep>();
}

/// <summary>
/// A step of a flow (§6.20). The common fields apply to every kind; the rest apply to one kind and are null or defaulted for the
/// others. <see cref="Key"/> names the step in tokens (<c>{{details.description}}</c>) and is unique within the flow.
/// </summary>
public class RequestFlowStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public RequestFlow Flow { get; set; } = null!;
    /// <summary>Lower-case letters, digits and hyphens; how tokens name the step.</summary>
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public RequestStepKind Kind { get; set; } = RequestStepKind.Form;
    public int DisplayOrder { get; set; }

    // --- Form and Url: who performs it (null = the person who logged the request).
    public Guid? PerformedById { get; set; }
    public ApplicationUser? PerformedBy { get; set; }

    // --- Url: the address, with tokens allowed.
    public string? Url { get; set; }

    // --- Task: what the task is created with.
    /// <summary>The department the task is filed in; null means the flow's.</summary>
    public Guid? TaskDepartmentId { get; set; }
    public Department? TaskDepartment { get; set; }
    public TaskType TaskType { get; set; } = TaskType.Task;
    public TaskPriority TaskPriority { get; set; } = TaskPriority.Medium;
    public string? TitleTemplate { get; set; }
    public string? DescriptionTemplate { get; set; }
    /// <summary>A Date field of an earlier form whose answer is the task's due date.</summary>
    public Guid? DueDateFieldId { get; set; }
    public RequestFormField? DueDateField { get; set; }
    /// <summary>An Asset field of an earlier form whose answer is the task's asset.</summary>
    public Guid? AssetFieldId { get; set; }
    public RequestFormField? AssetField { get; set; }
    /// <summary>A Project field of an earlier form whose answer is the task's project.</summary>
    public Guid? ProjectFieldId { get; set; }
    public RequestFormField? ProjectField { get; set; }
    /// <summary>An Urgency field of an earlier form whose answer is the task's priority; <see cref="TaskPriority"/> applies without one, or when it wasn't answered.</summary>
    public Guid? PriorityFieldId { get; set; }
    public RequestFormField? PriorityField { get; set; }
    public RequesteeSource RequesteeSource { get; set; } = RequesteeSource.Requester;
    /// <summary>A User field of an earlier form whose answer is the task's requestee, when <see cref="RequesteeSource"/> is Field.</summary>
    public Guid? RequesteeFieldId { get; set; }
    public RequestFormField? RequesteeField { get; set; }
    /// <summary>Copy the files of every earlier form's Attachment fields to the task.</summary>
    public bool CopyAllAttachments { get; set; }
    /// <summary>Or copy the files of one form step's Attachment fields.</summary>
    public Guid? CopyAttachmentsFromStepId { get; set; }
    public RequestFlowStep? CopyAttachmentsFromStep { get; set; }

    // --- Action: which library action runs, and what its parameters are bound to.
    public Guid? ActionId { get; set; }
    public RequestAction? Action { get; set; }

    /// <summary>The steps this one waits for (the rows whose StepId is this step).</summary>
    public ICollection<RequestFlowStepDependency> Dependencies { get; set; } = new List<RequestFlowStepDependency>();
    public ICollection<RequestFormField> Fields { get; set; } = new List<RequestFormField>();
    public ICollection<RequestFlowApprovalStage> Stages { get; set; } = new List<RequestFlowApprovalStage>();
    /// <summary>A Task step's predefined assignees; none means the task waits unassigned.</summary>
    public ICollection<RequestFlowStepAssignee> Assignees { get; set; } = new List<RequestFlowStepAssignee>();
    public ICollection<RequestFlowStepActionInput> ActionInputs { get; set; } = new List<RequestFlowStepActionInput>();
}

/// <summary>
/// "Step X can't start before step Y" (§6.20). <see cref="RequiredOutcome"/>, only when Y is an Approval, hangs X on how it ended:
/// null means either way (Y completed or, for an approval, accepted).
/// </summary>
public class RequestFlowStepDependency
{
    public Guid StepId { get; set; }
    public RequestFlowStep Step { get; set; } = null!;
    public Guid DependsOnStepId { get; set; }
    public RequestFlowStep DependsOnStep { get; set; } = null!;
    public RequestOutcome? RequiredOutcome { get; set; }
}

/// <summary>One question of a Form step (§6.20). <see cref="Key"/> names it in tokens, unique within the step.</summary>
public class RequestFormField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }
    public RequestFlowStep Step { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Shown under the prompt: what a good answer looks like.</summary>
    public string? HelpText { get; set; }
    public RequestFieldType FieldType { get; set; } = RequestFieldType.Text;
    public bool IsRequired { get; set; }
    /// <summary>The answers a Choice field offers, in order; empty for every other type.</summary>
    public List<string> Choices { get; set; } = [];
    /// <summary>What an Asset, Asset type, Project or User field offers; null for the other types.</summary>
    public RequestPickerScope? PickerScope { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>One stage of an Approval step (§6.20): its approvers, and whether one or all of them must approve.</summary>
public class RequestFlowApprovalStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }
    public RequestFlowStep Step { get; set; } = null!;
    /// <summary>1, 2, 3...: stages open in this order.</summary>
    public int StageOrder { get; set; }
    public ApprovalRule Rule { get; set; } = ApprovalRule.Any;

    public ICollection<RequestFlowApprover> Approvers { get; set; } = new List<RequestFlowApprover>();
}

/// <summary>Who approves in a stage (§6.20): a person, or everyone holding a role in a department (fixed, or the requester's own).</summary>
public class RequestFlowApprover
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StageId { get; set; }
    public RequestFlowApprovalStage Stage { get; set; } = null!;
    public ApproverKind Kind { get; set; } = ApproverKind.Person;
    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? RoleId { get; set; }
    public ApplicationRole? Role { get; set; }
}

/// <summary>A person a Task step's task is assigned to when it is created (§6.20).</summary>
public class RequestFlowStepAssignee
{
    public Guid StepId { get; set; }
    public RequestFlowStep Step { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

/// <summary>What an Action step passes for one of its action's parameters (§6.20): a template with tokens, rendered when the step runs.</summary>
public class RequestFlowStepActionInput
{
    public Guid StepId { get; set; }
    public RequestFlowStep Step { get; set; } = null!;
    public string ParameterKey { get; set; } = string.Empty;
    public string? ValueTemplate { get; set; }
}

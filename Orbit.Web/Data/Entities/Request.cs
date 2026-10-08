namespace Orbit.Data.Entities;

/// <summary>
/// A request someone logged through a flow (spec §6.20): R-26-00012. It is filed with the flow's department, and it walks the
/// flow's steps - one <see cref="RequestStep"/> per flow step, created when the request is logged - until every step has settled.
/// </summary>
public class Request
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = string.Empty;
    public Guid FlowId { get; set; }
    public RequestFlow Flow { get; set; } = null!;
    /// <summary>The flow's category's department, copied so lists and scopes don't join through the flow.</summary>
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public Guid RequesterId { get; set; }
    public ApplicationUser Requester { get; set; } = null!;
    public RequestStatus Status { get; set; } = RequestStatus.InProgress;
    /// <summary>Set by the page that logs it, so a double click or a resubmitted page logs one request.</summary>
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<RequestStep> Steps { get; set; } = new List<RequestStep>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();

    public bool IsOpen => Status == RequestStatus.InProgress;
}

/// <summary>
/// One flow step as it stands in one request (§6.20). What it holds depends on the kind: a form's answers, an approval's decisions,
/// a task step's task, an action's output or error. <see cref="AssignedToId"/> is who performs a form or web-page step.
/// </summary>
public class RequestStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Request Request { get; set; } = null!;
    public Guid FlowStepId { get; set; }
    public RequestFlowStep FlowStep { get; set; } = null!;
    public RequestStepStatus Status { get; set; } = RequestStepStatus.Pending;
    public Guid? AssignedToId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    /// <summary>When the step became Ready - for an action, when a run claimed it.</summary>
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedById { get; set; }
    public ApplicationUser? CompletedBy { get; set; }
    /// <summary>What an action's script logged and returned.</summary>
    public string? Output { get; set; }
    /// <summary>Why the step failed, for a manager to read before retrying or skipping it.</summary>
    public string? Error { get; set; }

    public ICollection<RequestFormAnswer> Answers { get; set; } = new List<RequestFormAnswer>();
    public ICollection<RequestApproval> Approvals { get; set; } = new List<RequestApproval>();
}

/// <summary>
/// One field's answer in one request (§6.20). <see cref="Value"/> is the canonical text - or the label of the asset, asset type,
/// project or person picked, kept even if that record is later deleted; the id columns link to it. An Attachment field has one row
/// per file.
/// </summary>
public class RequestFormAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }
    public RequestStep Step { get; set; } = null!;
    public Guid FieldId { get; set; }
    public RequestFormField Field { get; set; } = null!;
    public string? Value { get; set; }
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    public Guid? AssetTypeId { get; set; }
    public AssetType? AssetType { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public Guid? AttachmentId { get; set; }
    public Attachment? Attachment { get; set; }
}

/// <summary>One approver's decision in one stage of one request's approval step (§6.20). Rows are created when the stage opens.</summary>
public class RequestApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }
    public RequestStep Step { get; set; } = null!;
    public int StageOrder { get; set; }
    public Guid ApproverId { get; set; }
    public ApplicationUser Approver { get; set; } = null!;
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public string? Comment { get; set; }
    public DateTime? DecidedAt { get; set; }
}

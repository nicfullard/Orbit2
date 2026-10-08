using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>A request category's own fields (§6.20). The department is chosen once, when the category is created.</summary>
public sealed class RequestCategoryInput
{
    /// <summary>On create: the owning department; null = the caller's own. Ignored on update.</summary>
    public Guid? DepartmentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public RequestColour Colour { get; set; } = RequestColour.Blue;
}

/// <summary>A request flow's own fields (§6.20).</summary>
public sealed class RequestFlowInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Adding a step to a flow (§6.20): its kind is fixed once added; the key is derived from the title when blank.</summary>
public sealed class RequestStepAddInput
{
    public RequestStepKind Kind { get; set; } = RequestStepKind.Form;
    public string Title { get; set; } = string.Empty;
    public string? Key { get; set; }
}

/// <summary>One dependency of a step (§6.20): the step it waits for and, for an approval, the outcome it needs.</summary>
public sealed record RequestDependencyInput(Guid DependsOnStepId, RequestOutcome? RequiredOutcome);

/// <summary>
/// A step's settings (§6.20): the common ones, its dependencies, and the ones for its kind - the service reads only what the kind
/// uses, so a form may post everything.
/// </summary>
public sealed class RequestStepInput
{
    public string Title { get; set; } = string.Empty;
    public string? Key { get; set; }
    public IReadOnlyList<RequestDependencyInput> Dependencies { get; set; } = [];

    // Form and Url
    public Guid? PerformedById { get; set; }

    // Url
    public string? Url { get; set; }

    // Task
    public Guid? TaskDepartmentId { get; set; }
    public TaskType TaskType { get; set; } = TaskType.Task;
    public TaskPriority TaskPriority { get; set; } = TaskPriority.Medium;
    public string? TitleTemplate { get; set; }
    public string? DescriptionTemplate { get; set; }
    public Guid? DueDateFieldId { get; set; }
    public Guid? AssetFieldId { get; set; }
    public Guid? ProjectFieldId { get; set; }
    /// <summary>An Urgency field the priority comes from; <see cref="TaskPriority"/> applies without one.</summary>
    public Guid? PriorityFieldId { get; set; }
    public RequesteeSource RequesteeSource { get; set; } = RequesteeSource.Requester;
    public Guid? RequesteeFieldId { get; set; }
    public bool CopyAllAttachments { get; set; }
    public Guid? CopyAttachmentsFromStepId { get; set; }
    public IReadOnlyList<Guid> AssigneeIds { get; set; } = [];

    // Action
    public Guid? ActionId { get; set; }
    /// <summary>By parameter key.</summary>
    public IReadOnlyDictionary<string, string?> ActionInputs { get; set; } = new Dictionary<string, string?>();
}

/// <summary>One field of a Form step (§6.20). <see cref="Choices"/> only for a Choice; <see cref="PickerScope"/> only for Asset, Asset type, Project and User.</summary>
public sealed class RequestFieldInput
{
    public string Prompt { get; set; } = string.Empty;
    public string? Key { get; set; }
    public string? HelpText { get; set; }
    public RequestFieldType FieldType { get; set; } = RequestFieldType.Text;
    public bool IsRequired { get; set; }
    public IReadOnlyList<string> Choices { get; set; } = [];
    public RequestPickerScope? PickerScope { get; set; }
}

/// <summary>
/// One asset type an Asset type field offers (§6.20), with the group the list shows it under: its category for the flow's
/// department's own, department and category for the whole company's; blank for none.
/// </summary>
public sealed record RequestAssetTypeChoice(Guid Id, string Name, string Group);

/// <summary>One approver of a stage (§6.20): a person, or a role in a department (fixed, or the requester's own).</summary>
public sealed class RequestApproverInput
{
    public ApproverKind Kind { get; set; } = ApproverKind.Person;
    public Guid? UserId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? RoleId { get; set; }
}

/// <summary>An action in the library (§6.20) with its parameters, in order.</summary>
public sealed class RequestActionInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ActionRunsOn RunsOn { get; set; } = ActionRunsOn.Web;
    public Guid? AgentId { get; set; }
    public string Script { get; set; } = string.Empty;
    public IReadOnlyList<RequestActionParameterInput> Parameters { get; set; } = [];
}

public sealed record RequestActionParameterInput(string? Key, string Label);

/// <summary>One department's offered categories on the Requests page, each with its offered flows in order.</summary>
public sealed record RequestCatalogueSection(Department Department, IReadOnlyList<RequestCategory> Categories);

/// <summary>Someone a form's Person field may name (§6.20).</summary>
public sealed record RequestPerson(Guid Id, string Name, string? Email);

/// <summary>A step addressed to the current person (§6.20) - a form to fill in, a page to open, an approval to give - for "Needs your action".</summary>
public sealed record RequestActionItem(Request Request, RequestStep Step, string What, DateTime Since);

/// <summary>The department requests list's filter (§6.20).</summary>
public sealed class RequestFilter
{
    public Guid? DepartmentId { get; set; }
    public RequestStatus? Status { get; set; }
    public string? Query { get; set; }
    public int Take { get; set; } = 100;
}

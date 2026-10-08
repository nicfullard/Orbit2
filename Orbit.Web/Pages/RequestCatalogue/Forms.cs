using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>A request category's own fields (spec §6.20). The department is chosen once, when the category is created.</summary>
public sealed class CategoryForm
{
    public Guid? DepartmentId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public RequestColour Colour { get; set; } = RequestColour.Blue;

    public RequestCategoryInput ToInput() => new()
    {
        DepartmentId = DepartmentId, Title = Title ?? string.Empty, Description = Description, Icon = Icon, Colour = Colour
    };

    public static CategoryForm From(RequestCategory c) => new()
    {
        DepartmentId = c.DepartmentId, Title = c.Title, Description = c.Description, Icon = c.Icon, Colour = c.Colour
    };
}

/// <summary>A flow's own fields (§6.20).</summary>
public sealed class FlowForm
{
    public string? Title { get; set; }
    public string? Description { get; set; }

    public RequestFlowInput ToInput() => new() { Title = Title ?? string.Empty, Description = Description };

    public static FlowForm From(RequestFlow f) => new() { Title = f.Title, Description = f.Description };
}

/// <summary>Adding a step (§6.20): its kind, title and key.</summary>
public sealed class StepAddForm
{
    public RequestStepKind Kind { get; set; } = RequestStepKind.Form;
    public string? Title { get; set; }
    public string? Key { get; set; }

    public RequestStepAddInput ToInput() => new() { Kind = Kind, Title = Title ?? string.Empty, Key = Key };
}

/// <summary>
/// A step's settings (§6.20) as the step page posts them: the common ones, the dependencies (a checkbox per other step, and an outcome
/// select beside each approval), and every kind's fields - the service reads the ones the kind uses.
/// </summary>
public sealed class StepForm
{
    public string? Title { get; set; }
    public string? Key { get; set; }
    public List<Guid> DependsOn { get; set; } = [];
    /// <summary>By the step depended on: "", "Accepted" or "Declined".</summary>
    public Dictionary<Guid, string?> Outcome { get; set; } = new();

    public Guid? PerformedById { get; set; }
    public string? Url { get; set; }

    public Guid? TaskDepartmentId { get; set; }
    public TaskType TaskType { get; set; } = TaskType.Task;
    public TaskPriority TaskPriority { get; set; } = TaskPriority.Medium;
    public string? TitleTemplate { get; set; }
    public string? DescriptionTemplate { get; set; }
    /// <summary>"none", "created" (the day the task is created), or the id of the Date field the due date comes from.</summary>
    public string? DueDate { get; set; } = "none";
    public Guid? AssetFieldId { get; set; }
    public Guid? ProjectFieldId { get; set; }
    public Guid? PriorityFieldId { get; set; }
    public RequesteeSource RequesteeSource { get; set; } = RequesteeSource.Requester;
    public Guid? RequesteeFieldId { get; set; }
    /// <summary>"none", "all", or the id of the form step whose files are copied.</summary>
    public string? CopyAttachments { get; set; } = "none";
    public List<Guid> AssigneeIds { get; set; } = [];

    public Guid? ActionId { get; set; }
    public Dictionary<string, string?> ActionInputs { get; set; } = new();

    public RequestStepInput ToInput() => new()
    {
        Title = Title ?? string.Empty,
        Key = Key,
        Dependencies = DependsOn.Distinct()
            .Select(id => new RequestDependencyInput(id, Enum.TryParse<RequestOutcome>(Outcome.GetValueOrDefault(id), true, out var o) ? o : null))
            .ToList(),
        PerformedById = PerformedById,
        Url = Url,
        TaskDepartmentId = TaskDepartmentId,
        TaskType = TaskType,
        TaskPriority = TaskPriority,
        TitleTemplate = TitleTemplate,
        DescriptionTemplate = DescriptionTemplate,
        DueOnCreation = DueDate == "created",
        DueDateFieldId = Guid.TryParse(DueDate, out var dueField) ? dueField : null,
        AssetFieldId = AssetFieldId,
        ProjectFieldId = ProjectFieldId,
        PriorityFieldId = PriorityFieldId,
        RequesteeSource = RequesteeSource,
        RequesteeFieldId = RequesteeFieldId,
        CopyAllAttachments = CopyAttachments == "all",
        CopyAttachmentsFromStepId = Guid.TryParse(CopyAttachments, out var from) ? from : null,
        AssigneeIds = AssigneeIds,
        ActionId = ActionId,
        ActionInputs = ActionInputs
    };

    public static StepForm From(RequestFlowStep s) => new()
    {
        Title = s.Title,
        Key = s.Key,
        DependsOn = s.Dependencies.Select(d => d.DependsOnStepId).ToList(),
        Outcome = s.Dependencies.ToDictionary(d => d.DependsOnStepId, d => d.RequiredOutcome?.ToString()),
        PerformedById = s.PerformedById,
        Url = s.Url,
        TaskDepartmentId = s.TaskDepartmentId,
        TaskType = s.TaskType,
        TaskPriority = s.TaskPriority,
        TitleTemplate = s.TitleTemplate,
        DescriptionTemplate = s.DescriptionTemplate,
        DueDate = s.DueOnCreation ? "created" : s.DueDateFieldId?.ToString() ?? "none",
        AssetFieldId = s.AssetFieldId,
        ProjectFieldId = s.ProjectFieldId,
        PriorityFieldId = s.PriorityFieldId,
        RequesteeSource = s.RequesteeSource,
        RequesteeFieldId = s.RequesteeFieldId,
        CopyAttachments = s.CopyAllAttachments ? "all" : s.CopyAttachmentsFromStepId?.ToString() ?? "none",
        AssigneeIds = s.Assignees.Select(a => a.UserId).ToList(),
        ActionId = s.ActionId,
        ActionInputs = s.ActionInputs.ToDictionary(i => i.ParameterKey, i => i.ValueTemplate)
    };
}

/// <summary>One field of a form step (§6.20). A Choice field's answers are typed one per line.</summary>
public sealed class FieldForm
{
    public string? Prompt { get; set; }
    public string? Key { get; set; }
    public string? HelpText { get; set; }
    public RequestFieldType FieldType { get; set; } = RequestFieldType.Text;
    public bool IsRequired { get; set; }
    public string? Choices { get; set; }
    public RequestPickerScope? PickerScope { get; set; }

    public RequestFieldInput ToInput() => new()
    {
        Prompt = Prompt ?? string.Empty,
        Key = Key,
        HelpText = HelpText,
        FieldType = FieldType,
        IsRequired = IsRequired,
        Choices = (Choices ?? string.Empty).Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
        PickerScope = PickerScope
    };

    public static FieldForm From(RequestFormField f) => new()
    {
        Prompt = f.Prompt, Key = f.Key, HelpText = f.HelpText, FieldType = f.FieldType, IsRequired = f.IsRequired,
        Choices = string.Join("\n", f.Choices), PickerScope = f.PickerScope
    };
}

/// <summary>One approver of a stage (§6.20).</summary>
public sealed class ApproverForm
{
    public ApproverKind Kind { get; set; } = ApproverKind.Person;
    public Guid? UserId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? RoleId { get; set; }

    public RequestApproverInput ToInput() => new() { Kind = Kind, UserId = UserId, DepartmentId = DepartmentId, RoleId = RoleId };
}

/// <summary>The category fields partial: the form's values, the prefix its inputs post under, and the departments to choose from on create.</summary>
public sealed class CategoryFieldsVm
{
    public required CategoryForm Form { get; init; }
    public required string Prefix { get; init; }
    /// <summary>Only on create, and only for someone who may configure more than their own department.</summary>
    public IReadOnlyList<Department>? Departments { get; init; }
}

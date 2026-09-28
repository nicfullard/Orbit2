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

/// <summary>A request option's fields. An unticked checkbox posts nothing, so <see cref="AllowAttachments"/> binds false then.</summary>
public sealed class OptionForm
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public RequestOptionKind Kind { get; set; } = RequestOptionKind.Flow;
    public string? Url { get; set; }
    public bool AllowAttachments { get; set; }
    public TaskType TaskType { get; set; } = TaskType.Task;

    public RequestOptionInput ToInput() => new()
    {
        Title = Title ?? string.Empty, Description = Description, Kind = Kind, Url = Url, AllowAttachments = AllowAttachments, TaskType = TaskType
    };

    public static OptionForm From(RequestOption o) => new()
    {
        Title = o.Title, Description = o.Description, Kind = o.Kind, Url = o.Url, AllowAttachments = o.AllowAttachments, TaskType = o.TaskType
    };

    /// <summary>What a new option starts as: a flow of Task type with a files step.</summary>
    public static OptionForm New() => new() { AllowAttachments = true };
}

/// <summary>One question of a flow. A Choice question's answers are typed one per line.</summary>
public sealed class QuestionForm
{
    public string? Prompt { get; set; }
    public string? HelpText { get; set; }
    public RequestQuestionType QuestionType { get; set; } = RequestQuestionType.Text;
    public bool IsRequired { get; set; }
    public string? Choices { get; set; }
    public bool SetsDueDate { get; set; }

    public RequestQuestionInput ToInput() => new()
    {
        Prompt = Prompt ?? string.Empty,
        HelpText = HelpText,
        QuestionType = QuestionType,
        IsRequired = IsRequired,
        Choices = (Choices ?? string.Empty).Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
        // Only a Date question can set the due date; a box left ticked under another type (it is hidden, not cleared) doesn't count.
        SetsDueDate = SetsDueDate && QuestionType == RequestQuestionType.Date
    };
}

/// <summary>The option fields partial: the form's values and the prefix its inputs post under.</summary>
public sealed class OptionFieldsVm
{
    public required OptionForm Form { get; init; }
    public required string Prefix { get; init; }
}

/// <summary>The category fields partial: the form's values, the prefix its inputs post under, and the departments to choose from on create.</summary>
public sealed class CategoryFieldsVm
{
    public required CategoryForm Form { get; init; }
    public required string Prefix { get; init; }
    /// <summary>Only on create, and only for someone who may configure more than their own department.</summary>
    public IReadOnlyList<Department>? Departments { get; init; }
}

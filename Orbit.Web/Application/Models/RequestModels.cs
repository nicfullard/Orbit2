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

/// <summary>A request option's own fields (§6.20): a Flow's task type and attachments step, or a Link's address.</summary>
public sealed class RequestOptionInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RequestOptionKind Kind { get; set; } = RequestOptionKind.Flow;
    public string? Url { get; set; }
    public bool AllowAttachments { get; set; } = true;
    public TaskType TaskType { get; set; } = TaskType.Task;
}

/// <summary>One question of a request flow (§6.20). <see cref="Choices"/> only for a Choice, <see cref="SetsDueDate"/> only for a Date.</summary>
public sealed class RequestQuestionInput
{
    public string Prompt { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public RequestQuestionType QuestionType { get; set; } = RequestQuestionType.Text;
    public bool IsRequired { get; set; }
    public IReadOnlyList<string> Choices { get; set; } = [];
    public bool SetsDueDate { get; set; }
}

/// <summary>A category in the configuration list, with how many options it has and how many of them are live.</summary>
public sealed record RequestCategoryListItem(RequestCategory Category, int OptionCount, int LiveOptionCount);

/// <summary>One department's live categories on the Requests page, each with its live options in order.</summary>
public sealed record RequestCatalogueSection(Department Department, IReadOnlyList<RequestCategory> Categories);

/// <summary>A request the current user logged, or that was logged for them (§6.20), for "Your requests": the task, and whether they may open it.</summary>
public sealed record MyRequest(TaskItem Task, bool CanOpen);

/// <summary>Someone a request can be logged for (a flow's User question, §6.20).</summary>
public sealed record RequestPerson(Guid Id, string Name, string? Email);

/// <summary>
/// What <c>TaskService.CreateRequestAsync</c> files (§6.20): the task a request flow composed. The department is the flow's; the
/// requester is the actor; there is never an assignee, project or sprint.
/// </summary>
public sealed class RequestTaskInput
{
    public required Guid DepartmentId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;
    public TaskType Type { get; init; } = TaskType.Task;
    public DateOnly? DueDate { get; init; }
    public Guid? AssetId { get; init; }
    /// <summary>Who the request is for (the flow's User question); null when it asks none. Must be someone the requester may log for.</summary>
    public Guid? RequestedForId { get; init; }
    public string? IdempotencyKey { get; init; }
    /// <summary>What the Created audit entry records about the flow: its category and option, by id and title.</summary>
    public required object RequestDetails { get; init; }
}

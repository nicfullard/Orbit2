namespace Orbit.Data.Entities;

/// <summary>
/// A heading on the Requests page (spec §6.20) - "Report a problem", "Request something new" - grouping the options people pick
/// from. Owned by one department, set when it is created and never changed: every task logged through its options is filed in
/// that department, and requests.configure reaching it manages the category, its options and their questions.
/// </summary>
public class RequestCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>One of the names in <c>RequestIcons</c>; an unknown name draws the default icon.</summary>
    public string Icon { get; set; } = string.Empty;
    public RequestColour Colour { get; set; } = RequestColour.Blue;
    public int DisplayOrder { get; set; }
    /// <summary>Archived: hidden from the Requests page with all its options, kept for its configuration.</summary>
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestOption> Options { get; set; } = new List<RequestOption>();
}

/// <summary>
/// One choice under a request category (§6.20): a flow of questions that logs a task, or a link to a page elsewhere (self-service).
/// Tasks don't reference it - a logged request keeps its questions and answers as text - so it can be changed or deleted freely.
/// </summary>
public class RequestOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public RequestCategory Category { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RequestOptionKind Kind { get; set; } = RequestOptionKind.Flow;
    /// <summary>A Link's absolute http(s) address; null for a Flow.</summary>
    public string? Url { get; set; }
    /// <summary>A Flow offers an optional "Attach files" step before its review.</summary>
    public bool AllowAttachments { get; set; } = true;
    /// <summary>The type every task logged through this flow gets - set by whoever configures it, never chosen by the requester.</summary>
    public TaskType TaskType { get; set; } = TaskType.Task;
    public int DisplayOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestQuestion> Questions { get; set; } = new List<RequestQuestion>();
}

/// <summary>One step of a request flow (§6.20). Its answer goes into the task's description; some also fill a task field.</summary>
public class RequestQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OptionId { get; set; }
    public RequestOption Option { get; set; } = null!;
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Shown under the prompt: what a good answer looks like.</summary>
    public string? HelpText { get; set; }
    public RequestQuestionType QuestionType { get; set; } = RequestQuestionType.Text;
    public bool IsRequired { get; set; }
    /// <summary>The answers a Choice question offers, in order; empty for every other type.</summary>
    public List<string> Choices { get; set; } = [];
    /// <summary>A Date question whose answer becomes the task's due date ("When do you need it by?"). At most one per flow.</summary>
    public bool SetsDueDate { get; set; }
    public int DisplayOrder { get; set; }
}

using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application.Requests;

/// <summary>The raw answer to one question as the form posts it: the typed or chosen text, and for an Asset question the asset picked.</summary>
public sealed record RequestAnswerInput(string? Value, Guid? AssetId = null);

/// <summary>
/// A cleaned answer: <see cref="Value"/> in its canonical form (see <see cref="RequestRules"/>), <see cref="AssetId"/> for a picked asset,
/// <see cref="UserId"/> for the person a User question names (whose name is then the value).
/// </summary>
public sealed record RequestAnswer(RequestQuestion Question, string? Value, Guid? AssetId = null, Guid? UserId = null);

/// <summary>What a request is filed as: the task's title and description, and the task fields its answers fill.</summary>
public sealed record ComposedRequest(string Title, string Description, TaskPriority Priority, DateOnly? DueDate, Guid? AssetId, Guid? RequesteeId);

/// <summary>A question definition after its checks.</summary>
public sealed record QuestionDefinition(string Prompt, string? HelpText, RequestQuestionType Type, bool IsRequired, List<string> Choices, bool SetsDueDate);

/// <summary>
/// Request flows (spec §6.20) as pure functions, so the rules are unit-tested: what a category, option and question may hold, when an
/// option is offered, how answers are checked, and how they become a task. An answer is kept in one canonical form per question type:
/// Text trimmed with its line breaks as \n, Number an invariant decimal ("2.5"), Date "yyyy-MM-dd", Urgency a <see cref="TaskPriority"/>
/// name, Choice one of the choices in its own spelling, Asset the picked asset and/or a description of one that isn't listed, User
/// the person's id (with their name as the value) - one of the people the requester may log for.
/// </summary>
public static class RequestRules
{
    public const int MaxCategoryTitleLength = 100;
    public const int MaxOptionTitleLength = 150;
    public const int MaxDescriptionLength = 500;
    public const int MaxUrlLength = 2000;
    public const int MaxPromptLength = 300;
    public const int MaxHelpTextLength = 500;
    public const int MaxQuestions = 30;
    public const int MaxChoices = 50;
    public const int MaxChoiceLength = 200;
    public const int MaxTextAnswerLength = 4000;
    public const int MaxAssetTextLength = 500;
    /// <summary>How much of the first text answer goes into the task's title.</summary>
    public const int MaxTitleLineLength = 120;
    /// <summary>A Choice with at most this many answers is shown as buttons, a longer one as a list.</summary>
    public const int ChoiceButtonsUpTo = 6;
    private const int MaxTaskTitleLength = 300;

    // ---------------------------------------------------------------- configuration

    public static string RequireTitle(string? title, int max)
    {
        var t = title?.Trim();
        if (string.IsNullOrEmpty(t)) throw new ValidationException("A title is required.");
        if (t.Length > max) throw new ValidationException($"The title must be {max} characters or fewer.");
        return t;
    }

    /// <summary>Trimmed, null when blank, refused when longer than <paramref name="max"/>.</summary>
    public static string? Clean(string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > max) throw new ValidationException($"{label} must be {max} characters or fewer.");
        return v;
    }

    /// <summary>A category icon: one of <see cref="RequestIcons.Choices"/>; blank takes the default.</summary>
    public static string CleanIcon(string? icon)
    {
        var i = icon?.Trim();
        if (string.IsNullOrEmpty(i)) return RequestIcons.Default;
        return RequestIcons.IsChoice(i) ? i : throw new ValidationException("Choose one of the icons offered.");
    }

    public static RequestColour CleanColour(RequestColour colour) =>
        Enum.IsDefined(colour) ? colour : throw new ValidationException("Choose one of the colours offered.");

    /// <summary>
    /// REQ-001: a link's address must be a full http or https web address - no relative paths, no <c>javascript:</c>, <c>file:</c> or
    /// <c>data:</c> - since it is rendered as a link that opens in a new tab.
    /// </summary>
    public static string CleanUrl(string? url)
    {
        var u = url?.Trim();
        if (string.IsNullOrEmpty(u)) throw new ValidationException("A link needs a web address.");
        if (u.Length > MaxUrlLength) throw new ValidationException($"The web address must be {MaxUrlLength} characters or fewer.");
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
            throw new ValidationException("The web address must be a full address starting with https:// (or http://).");
        return u;
    }

    /// <summary>
    /// An option's fields, checked: a Link needs its address (and has no task type or attachments step), a Flow has neither address nor
    /// link. Returns the cleaned title, description and address.
    /// </summary>
    public static (string Title, string? Description, string? Url) ValidateOption(RequestOptionInput input)
    {
        if (!Enum.IsDefined(input.Kind)) throw new ValidationException("Choose what the option does.");
        if (!Enum.IsDefined(input.TaskType)) throw new ValidationException("Choose one of the task types offered.");
        var title = RequireTitle(input.Title, MaxOptionTitleLength);
        var description = Clean(input.Description, MaxDescriptionLength, "The description");
        var url = input.Kind == RequestOptionKind.Link ? CleanUrl(input.Url) : null;
        return (title, description, url);
    }

    /// <summary>
    /// REQ-002: one question's definition. The prompt is required; a Choice needs two to <see cref="MaxChoices"/> distinct answers
    /// (blank lines dropped, ignoring case when comparing); only a Date question can set the due date.
    /// </summary>
    public static QuestionDefinition ValidateQuestion(RequestQuestionInput input)
    {
        if (!Enum.IsDefined(input.QuestionType)) throw new ValidationException("Choose one of the question types offered.");
        var prompt = input.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt)) throw new ValidationException("A question needs its wording.");
        if (prompt.Length > MaxPromptLength) throw new ValidationException($"A question must be {MaxPromptLength} characters or fewer.");
        var help = Clean(input.HelpText, MaxHelpTextLength, "The help text");
        if (input.SetsDueDate && input.QuestionType != RequestQuestionType.Date)
            throw new ValidationException("Only a Date question can set the task's due date.");

        var choices = new List<string>();
        if (input.QuestionType == RequestQuestionType.Choice)
        {
            foreach (var raw in input.Choices)
            {
                var c = raw?.Trim();
                if (string.IsNullOrEmpty(c)) continue;
                if (c.Length > MaxChoiceLength) throw new ValidationException($"An answer to choose must be {MaxChoiceLength} characters or fewer: \"{c[..20]}...\".");
                if (choices.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                    throw new ValidationException($"\"{c}\" is listed twice.");
                choices.Add(c);
            }
            if (choices.Count < 2) throw new ValidationException("A Choice question needs at least two answers to choose from, one per line.");
            if (choices.Count > MaxChoices) throw new ValidationException($"A Choice question can offer at most {MaxChoices} answers.");
        }
        return new QuestionDefinition(prompt, help, input.QuestionType, input.IsRequired, choices, input.SetsDueDate);
    }

    /// <summary>
    /// REQ-002, REQ-008: a flow's questions together. Each task field is filled by one question at most - one Urgency (the priority),
    /// one Asset (the task's asset), one User (who it is for), one Date that sets the due date - and a flow has at most
    /// <see cref="MaxQuestions"/> questions.
    /// </summary>
    public static void CheckFlow(IReadOnlyCollection<RequestQuestion> questions)
    {
        if (questions.Count > MaxQuestions) throw new ValidationException($"A flow can have at most {MaxQuestions} questions.");
        if (questions.Count(q => q.QuestionType == RequestQuestionType.Urgency) > 1)
            throw new ValidationException("A flow can have only one Urgency question: its answer is the task's priority.");
        if (questions.Count(q => q.QuestionType == RequestQuestionType.Asset) > 1)
            throw new ValidationException("A flow can have only one Asset question: the asset picked is the task's asset.");
        if (questions.Count(q => q.QuestionType == RequestQuestionType.User) > 1)
            throw new ValidationException("A flow can have only one User question: the person chosen is who the request is for.");
        if (questions.Count(q => q.QuestionType == RequestQuestionType.Date && q.SetsDueDate) > 1)
            throw new ValidationException("Only one Date question in a flow can set the task's due date.");
    }

    /// <summary>
    /// REQ-005: whether an option is offered on the Requests page - it, its category and the category's department aren't archived, and
    /// it can do something: a Link has its address, a Flow at least one question. An expression, so the same rule filters in SQL.
    /// </summary>
    public static readonly Expression<Func<RequestOption, bool>> Live = o =>
        !o.IsArchived && !o.Category.IsArchived && !o.Category.Department.IsArchived
        && ((o.Kind == RequestOptionKind.Link && o.Url != null) || (o.Kind == RequestOptionKind.Flow && o.Questions.Any()));

    private static readonly Func<RequestOption, bool> LiveCompiled = Live.Compile();

    /// <summary><see cref="Live"/> for an option in memory; needs its category, the category's department and its questions loaded.</summary>
    public static bool IsLive(RequestOption option) => LiveCompiled(option);

    // ---------------------------------------------------------------- answers

    /// <summary>
    /// REQ-003: every answer to a flow's questions, cleaned into its canonical form, and a message for each question whose answer was
    /// refused or is required and missing. An Asset question is answered by a picked asset, a description, or both. A due-date answer
    /// can't be before <paramref name="today"/>. A User question is answered by one of <paramref name="people"/> (id to name: whom
    /// the requester may log for, REQ-008). Unanswered optional questions have no answer.
    /// </summary>
    public static (IReadOnlyList<RequestAnswer> Answers, IReadOnlyDictionary<Guid, string> Errors) CleanAnswers(
        IEnumerable<RequestQuestion> questions, IReadOnlyDictionary<Guid, RequestAnswerInput> input, DateOnly today,
        IReadOnlyDictionary<Guid, string>? people = null)
    {
        var answers = new List<RequestAnswer>();
        var errors = new Dictionary<Guid, string>();
        foreach (var q in questions.OrderBy(q => q.DisplayOrder))
        {
            try
            {
                var answer = CleanAnswer(q, input.GetValueOrDefault(q.Id), today, people);
                if (answer is not null) answers.Add(answer);
                else if (q.IsRequired) errors[q.Id] = q.QuestionType switch
                {
                    RequestQuestionType.Asset => "Choose one of your assets, or describe the one you mean.",
                    RequestQuestionType.User => "Choose who it is for.",
                    _ => "This question needs an answer."
                };
            }
            catch (ValidationException ex)
            {
                errors[q.Id] = ex.Message;
            }
        }
        return (answers, errors);
    }

    /// <summary>One answer in its canonical form, or null when blank. Refuses a value the question can't take.</summary>
    public static RequestAnswer? CleanAnswer(RequestQuestion q, RequestAnswerInput? raw, DateOnly today, IReadOnlyDictionary<Guid, string>? people = null)
    {
        var text = raw?.Value?.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (q.QuestionType == RequestQuestionType.Asset)
        {
            var assetId = raw?.AssetId is Guid g && g != Guid.Empty ? g : (Guid?)null;
            if (text is { Length: > MaxAssetTextLength })
                throw new ValidationException($"The description must be {MaxAssetTextLength} characters or fewer.");
            if (assetId is null && string.IsNullOrEmpty(text)) return null;
            return new RequestAnswer(q, string.IsNullOrEmpty(text) ? null : text, assetId);
        }
        if (string.IsNullOrEmpty(text)) return null;

        switch (q.QuestionType)
        {
            case RequestQuestionType.Number:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    return new RequestAnswer(q, number.ToString("0.############################", CultureInfo.InvariantCulture));
                throw new ValidationException("Enter a number, like 3 or 2.5.");
            case RequestQuestionType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ValidationException("Enter a date, like 2026-09-30.");
                if (q.SetsDueDate && date < today) throw new ValidationException("The date can't be in the past.");
                return new RequestAnswer(q, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case RequestQuestionType.Urgency:
                if (text.All(char.IsLetter) && Enum.TryParse<TaskPriority>(text, true, out var priority) && Enum.IsDefined(priority))
                    return new RequestAnswer(q, priority.ToString());
                throw new ValidationException("Choose how urgent it is.");
            case RequestQuestionType.Choice:
                var choice = q.Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ValidationException("Choose one of the answers offered.");
                return new RequestAnswer(q, choice);
            case RequestQuestionType.User:
                if (Guid.TryParse(text, out var userId) && people is not null && people.TryGetValue(userId, out var name))
                    return new RequestAnswer(q, name, UserId: userId);
                throw new ValidationException("Choose one of the people offered.");
            default:
                if (text.Length > MaxTextAnswerLength)
                    throw new ValidationException($"The answer must be {MaxTextAnswerLength:N0} characters or fewer.");
                return new RequestAnswer(q, text);
        }
    }

    // ---------------------------------------------------------------- composing the task

    /// <summary>What each urgency means to the person asking: shown beside the choice and repeated in the task's description.</summary>
    public static string UrgencyMeaning(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => "No rush",
        TaskPriority.Medium => "It's slowing me down, but I can work around it",
        TaskPriority.High => "I can't do an important part of my work",
        TaskPriority.Critical => "Work has stopped for me or for several people",
        _ => priority.ToString()
    };

    /// <summary>
    /// REQ-004, REQ-008: the task a request is filed as. Title: the option's title and the first line of the first Text answer
    /// ("Something isn't working: The printer jams"), or "{option} for {person}" without one - the person the User question names,
    /// else the requester. Description: who logged it and through which option, then each answered question and its answer, in
    /// order. Priority: the Urgency answer, else Medium. Due date: the answer to the Date question that sets it. Asset: the asset
    /// picked. Requestee: the person the User question names. <paramref name="assetLabel"/> names the picked asset in the
    /// description.
    /// </summary>
    public static ComposedRequest Compose(
        RequestOption option, IReadOnlyList<RequestAnswer> answers, string requesterName, string? requesterDepartment, string? assetLabel = null)
    {
        var forWhom = answers.FirstOrDefault(a => a.Question.QuestionType == RequestQuestionType.User && a.UserId is not null);
        var firstText = answers.FirstOrDefault(a => a.Question.QuestionType == RequestQuestionType.Text && !string.IsNullOrEmpty(a.Value));
        var line = firstText is null ? string.Empty : FirstLine(firstText.Value!, MaxTitleLineLength);
        var title = Cap(line.Length > 0 ? $"{option.Title}: {line}" : $"{option.Title} for {forWhom?.Value ?? requesterName}", MaxTaskTitleLength);

        var d = new StringBuilder();
        d.Append("Logged through Requests by ").Append(requesterName);
        if (!string.IsNullOrEmpty(requesterDepartment)) d.Append(" (").Append(requesterDepartment).Append(')');
        d.Append(".\n");
        d.Append("Request: ").Append(option.Category.Title).Append(" › ").Append(option.Title).Append('\n');
        foreach (var a in answers.OrderBy(a => a.Question.DisplayOrder))
        {
            d.Append('\n').Append(a.Question.Prompt).Append('\n').Append(Display(a, assetLabel)).Append('\n');
        }

        var urgency = answers.FirstOrDefault(a => a.Question.QuestionType == RequestQuestionType.Urgency);
        var priority = urgency is not null && Enum.TryParse<TaskPriority>(urgency.Value, out var p) ? p : TaskPriority.Medium;
        var due = answers.FirstOrDefault(a => a.Question.QuestionType == RequestQuestionType.Date && a.Question.SetsDueDate);
        DateOnly? dueDate = due is not null ? DateOnly.ParseExact(due.Value!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var assetId = answers.FirstOrDefault(a => a.Question.QuestionType == RequestQuestionType.Asset && a.AssetId is not null)?.AssetId;

        return new ComposedRequest(title, d.ToString().TrimEnd('\n'), priority, dueDate, assetId, forWhom?.UserId);
    }

    /// <summary>An answer as the description and the review step show it.</summary>
    public static string Display(RequestAnswer answer, string? assetLabel = null)
    {
        switch (answer.Question.QuestionType)
        {
            case RequestQuestionType.Urgency when Enum.TryParse<TaskPriority>(answer.Value, out var p):
                return $"{p} - {UrgencyMeaning(p)}";
            case RequestQuestionType.Asset:
                var picked = answer.AssetId is null ? null : $"{assetLabel ?? "An asset from the register"} (linked to this task)";
                return picked is null ? answer.Value ?? string.Empty
                    : answer.Value is null ? picked
                    : $"{picked}\n{answer.Value}";
            default:
                return answer.Value ?? string.Empty;
        }
    }

    /// <summary>The first non-blank line of <paramref name="text"/>, its spaces collapsed, cut to <paramref name="max"/> characters.</summary>
    public static string FirstLine(string text, int max)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Cap(line, max);
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
}

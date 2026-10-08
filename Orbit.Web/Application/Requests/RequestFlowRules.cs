using System.Linq.Expressions;
using System.Text;
using Orbit.Application.Models;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Application.Requests;

/// <summary>A field definition after its checks (§6.20).</summary>
public sealed record RequestFieldDefinition(
    string Key, string Prompt, string? HelpText, RequestFieldType Type, bool IsRequired, IReadOnlyList<string> Choices, RequestPickerScope? Scope);

/// <summary>
/// Request flows (spec §6.20) as pure functions, so the rules are unit-tested: what a category, flow, step, field, stage and action may
/// hold; how keys are made; which dependencies are allowed (no cycles, an outcome only on an approval); what stops a step from being
/// ready; and when a flow is offered on the Requests page.
/// </summary>
public static class RequestFlowRules
{
    public const int MaxCategoryTitleLength = 100;
    public const int MaxFlowTitleLength = 150;
    public const int MaxStepTitleLength = 150;
    public const int MaxDescriptionLength = 500;
    public const int MaxKeyLength = 40;
    public const int MaxUrlLength = 2000;
    public const int MaxPromptLength = 300;
    public const int MaxHelpTextLength = 500;
    public const int MaxSteps = 50;
    public const int MaxFields = 30;
    public const int MaxChoices = 50;
    public const int MaxChoiceLength = 200;
    public const int MaxTitleTemplateLength = 500;
    public const int MaxTemplateLength = 4000;
    public const int MaxTextAnswerLength = 4000;
    public const int MaxStages = 10;
    public const int MaxApproversPerStage = 20;
    public const int MaxActionNameLength = 100;
    public const int MaxParameters = 20;
    public const int MaxParameterLabelLength = 150;
    public const int MaxCommentLength = 2000;
    /// <summary>How much of the first text answer goes into the request's title.</summary>
    public const int MaxTitleLineLength = 120;
    public const int MaxRequestTitleLength = 300;
    public const int MaxTaskTitleLength = 300;
    /// <summary>A Choice with at most this many answers is shown as buttons, a longer one as a list.</summary>
    public const int ChoiceButtonsUpTo = 6;
    /// <summary>The step key reserved for the request's own tokens ({{request.number}} and the rest).</summary>
    public const string RequestKey = "request";

    // ---------------------------------------------------------------- text

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

    // ---------------------------------------------------------------- keys

    /// <summary>
    /// REQ-009: a key names a step, field or parameter in tokens - lower-case letters, digits and hyphens, at most 40. Blank takes
    /// it from the title ("Product details" becomes "product-details"); "request" is reserved for the request's own tokens.
    /// </summary>
    public static string CleanKey(string? key, string? title)
    {
        var slug = Slug(string.IsNullOrWhiteSpace(key) ? title : key);
        if (slug.Length == 0) throw new ValidationException("A key is required: letters, digits and hyphens, like product-details.");
        if (slug.Length > MaxKeyLength) slug = slug[..MaxKeyLength].TrimEnd('-');
        if (slug == RequestKey) throw new ValidationException($"\"{RequestKey}\" is reserved for the request's own values; choose another key.");
        return slug;
    }

    /// <summary>Lower-case letters, digits and single hyphens, nothing else: "Who is it for?" becomes "who-is-it-for".</summary>
    public static string Slug(string? text)
    {
        var sb = new StringBuilder();
        var hyphen = false;
        foreach (var ch in (text ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                hyphen = false;
            }
            else if (!hyphen && sb.Length > 0)
            {
                sb.Append('-');
                hyphen = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    public static void RequireUniqueKey(IEnumerable<string> taken, string key, string what)
    {
        if (taken.Contains(key, StringComparer.Ordinal))
            throw new ValidationException($"{what} already uses the key \"{key}\"; choose another.");
    }

    // ---------------------------------------------------------------- steps

    /// <summary>
    /// A web-page step's address: a full http or https address - no relative paths, no <c>javascript:</c>, <c>file:</c> or <c>data:</c> -
    /// since it opens in a new tab. Tokens are allowed in it ({{details.code}}); they are checked as "x".
    /// </summary>
    public static string CleanUrl(string? url)
    {
        var u = url?.Trim();
        if (string.IsNullOrEmpty(u)) throw new ValidationException("A web address is required.");
        if (u.Length > MaxUrlLength) throw new ValidationException($"The web address must be {MaxUrlLength} characters or fewer.");
        var probe = RequestTokenRules.Render(u, _ => "x");
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
            throw new ValidationException("The web address must be a full address starting with https:// (or http://).");
        return u;
    }

    /// <summary>
    /// REQ-010: what keeps a step from being used, in words - the "Not configured" badge and what stops a flow being offered. Empty
    /// when the step is ready. An Action step's parameters are checked when its action is loaded with them.
    /// </summary>
    public static IReadOnlyList<string> Problems(RequestFlowStep step)
    {
        var problems = new List<string>();
        switch (step.Kind)
        {
            case RequestStepKind.Form:
                if (step.Fields.Count == 0) problems.Add("has no fields");
                break;
            case RequestStepKind.Approval:
                if (step.Stages.Count == 0) problems.Add("has no approval stage");
                foreach (var stage in step.Stages.OrderBy(s => s.StageOrder))
                    if (stage.Approvers.Count == 0) problems.Add($"stage {stage.StageOrder} has no approver");
                break;
            case RequestStepKind.Task:
                if (step.TaskDepartmentId is null) problems.Add("has no department");
                if (string.IsNullOrWhiteSpace(step.TitleTemplate)) problems.Add("has no task title");
                break;
            case RequestStepKind.Action:
                if (step.ActionId is null) problems.Add("has no action");
                else if (step.Action is not null)
                    foreach (var p in step.Action.Parameters.OrderBy(p => p.DisplayOrder))
                        if (!step.ActionInputs.Any(i => i.ParameterKey == p.Key)) problems.Add($"doesn't set the action's \"{p.Label}\"");
                break;
            case RequestStepKind.Url:
                if (string.IsNullOrWhiteSpace(step.Url)) problems.Add("has no web address");
                break;
        }
        return problems;
    }

    /// <summary>REQ-010: the kinds that someone performs by hand on the request page, so they may be addressed to a named person.</summary>
    public static bool IsPerformedByPerson(RequestStepKind kind) => kind is RequestStepKind.Form or RequestStepKind.Url;

    // ---------------------------------------------------------------- fields

    /// <summary>
    /// REQ-013: a field's definition - prompt and help text within their lengths; a Choice with two to 50 distinct answers (one per
    /// line) and no other type with any; a scope only for an Asset, Asset type, Project or User field, Held only for an Asset. An
    /// Urgency has neither: its answers are the four priorities.
    /// </summary>
    public static RequestFieldDefinition ValidateField(RequestFieldInput input, string? key = null)
    {
        var prompt = input.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt)) throw new ValidationException("The field needs a prompt: the question it asks.");
        if (prompt.Length > MaxPromptLength) throw new ValidationException($"The prompt must be {MaxPromptLength} characters or fewer.");
        var help = Clean(input.HelpText, MaxHelpTextLength, "The help text");
        if (!Enum.IsDefined(input.FieldType)) throw new ValidationException("Choose one of the field types offered.");

        var choices = input.Choices.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (input.FieldType == RequestFieldType.Choice)
        {
            if (choices.Count < 2) throw new ValidationException("A Choice field needs at least two answers, one per line.");
            if (choices.Count > MaxChoices) throw new ValidationException($"A Choice field can offer at most {MaxChoices} answers.");
            if (choices.Any(c => c.Length > MaxChoiceLength)) throw new ValidationException($"Each answer must be {MaxChoiceLength} characters or fewer.");
            if (choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Count) throw new ValidationException("The answers must be different from each other.");
        }
        else
        {
            choices = [];
        }

        RequestPickerScope? scope = null;
        if (input.FieldType is RequestFieldType.Asset or RequestFieldType.AssetType or RequestFieldType.Project or RequestFieldType.User)
        {
            scope = input.PickerScope ?? throw new ValidationException("Choose what the field offers: the department's, or the whole company's.");
            if (!Enum.IsDefined(scope.Value)) throw new ValidationException("Choose one of the scopes offered.");
            if (scope == RequestPickerScope.Held && input.FieldType != RequestFieldType.Asset)
                throw new ValidationException("Only an Asset field can offer the ones the person asking holds.");
        }
        return new RequestFieldDefinition(key ?? CleanKey(input.Key, prompt), prompt, help, input.FieldType, input.IsRequired, choices, scope);
    }

    // ---------------------------------------------------------------- dependencies

    /// <summary>
    /// REQ-011: a step's dependencies - each on another step of the same flow, none twice, an outcome only on an Approval step, and
    /// no way round in a circle (a step can't wait, however indirectly, for itself).
    /// </summary>
    public static void ValidateDependencies(IReadOnlyList<RequestFlowStep> steps, IReadOnlyList<RequestFlowStepDependency> dependencies)
    {
        var byId = steps.ToDictionary(s => s.Id);
        foreach (var d in dependencies)
        {
            if (!byId.TryGetValue(d.StepId, out var step) || !byId.TryGetValue(d.DependsOnStepId, out var on))
                throw new ValidationException("A step can only depend on a step of the same flow.");
            if (d.StepId == d.DependsOnStepId) throw new ValidationException($"\"{step.Title}\" can't depend on itself.");
            if (d.RequiredOutcome is not null && on.Kind != RequestStepKind.Approval)
                throw new ValidationException($"\"{on.Title}\" isn't an approval, so \"{step.Title}\" can't wait for it to be accepted or declined - only for it to finish.");
        }
        if (dependencies.Select(d => (d.StepId, d.DependsOnStepId)).Distinct().Count() != dependencies.Count)
            throw new ValidationException("A step can't depend on the same step twice.");

        // Kahn's sort: strip steps with nothing left to wait for; whatever remains is in a cycle.
        var waiting = steps.ToDictionary(s => s.Id, s => dependencies.Count(d => d.StepId == s.Id));
        var queue = new Queue<Guid>(waiting.Where(w => w.Value == 0).Select(w => w.Key));
        var done = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            done++;
            foreach (var d in dependencies.Where(d => d.DependsOnStepId == id))
                if (--waiting[d.StepId] == 0) queue.Enqueue(d.StepId);
        }
        if (done < steps.Count)
        {
            var stuck = steps.Where(s => waiting[s.Id] > 0).Select(s => $"\"{s.Title}\"").ToList();
            throw new ValidationException($"These dependencies go round in a circle: {string.Join(", ", stuck)}. A step can't wait for itself.");
        }
    }

    /// <summary>REQ-011: every step a step waits for, directly or through others - the steps whose values it may use.</summary>
    public static IReadOnlySet<Guid> TransitivePredecessors(Guid stepId, IEnumerable<RequestFlowStepDependency> dependencies)
    {
        var deps = dependencies.ToLookup(d => d.StepId, d => d.DependsOnStepId);
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>([stepId]);
        while (queue.Count > 0)
        {
            foreach (var on in deps[queue.Dequeue()])
                if (found.Add(on)) queue.Enqueue(on);
        }
        found.Remove(stepId);
        return found;
    }

    // ---------------------------------------------------------------- approvals

    /// <summary>REQ-010: an approver names what its kind needs - a person, or a role (and, unless the requester's, a department).</summary>
    public static void ValidateApprover(RequestApproverInput input)
    {
        if (!Enum.IsDefined(input.Kind)) throw new ValidationException("Choose one of the approver kinds offered.");
        switch (input.Kind)
        {
            case ApproverKind.Person when input.UserId is null || input.UserId == Guid.Empty:
                throw new ValidationException("Choose the person who approves.");
            case ApproverKind.RoleInDepartment when input.RoleId is null || input.DepartmentId is null:
                throw new ValidationException("Choose the role and the department whose people approve.");
            case ApproverKind.RoleInRequestersDepartment when input.RoleId is null:
                throw new ValidationException("Choose the role whose people in the requester's department approve.");
        }
    }

    // ---------------------------------------------------------------- actions

    public static string RequireActionName(string? name) => RequireTitle(name, MaxActionNameLength);

    /// <summary>REQ-010: an action's parameters - a label each, keys from the labels when blank, all different, at most 20.</summary>
    public static IReadOnlyList<(string Key, string Label)> ValidateParameters(IReadOnlyList<RequestActionParameterInput> parameters)
    {
        if (parameters.Count > MaxParameters) throw new ValidationException($"An action can take at most {MaxParameters} parameters.");
        var result = new List<(string, string)>();
        foreach (var p in parameters)
        {
            var label = p.Label?.Trim();
            if (string.IsNullOrEmpty(label)) throw new ValidationException("Each parameter needs a label.");
            if (label.Length > MaxParameterLabelLength) throw new ValidationException($"A parameter's label must be {MaxParameterLabelLength} characters or fewer.");
            var key = CleanKey(p.Key, label);
            RequireUniqueKey(result.Select(r => r.Item1), key, "Another parameter");
            result.Add((key, label));
        }
        return result;
    }

    // ---------------------------------------------------------------- the flow as a whole

    /// <summary>
    /// REQ-019: the form the requester fills in when they log the request - the first Form step in order that waits for nothing and is
    /// theirs to perform; null when the flow starts with something else, in which case logging it asks nothing.
    /// </summary>
    public static RequestFlowStep? StartForm(RequestFlow flow) =>
        flow.Steps.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Title)
            .FirstOrDefault(s => s.Kind == RequestStepKind.Form && s.PerformedById is null && s.Dependencies.Count == 0);

    /// <summary>
    /// REQ-019, the part a query can apply: a flow is offered while neither it, its category nor the category's department is archived
    /// and it has at least one step. <see cref="IsLive"/> adds that every step is configured.
    /// </summary>
    public static Expression<Func<RequestFlow, bool>> Offered =>
        f => !f.IsArchived && !f.Category.IsArchived && !f.Category.Department.IsArchived && f.Steps.Any();

    /// <summary>REQ-019: offered, and every step ready (<see cref="Problems"/> empty). Needs the category, its department and the steps' children loaded.</summary>
    public static bool IsLive(RequestFlow flow) =>
        !flow.IsArchived && !flow.Category.IsArchived && !flow.Category.Department.IsArchived
        && flow.Steps.Count > 0 && flow.Steps.All(s => Problems(s).Count == 0);

    /// <summary>The first non-blank line of <paramref name="text"/>, its spaces collapsed, cut to <paramref name="max"/> characters.</summary>
    public static string FirstLine(string text, int max)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Cap(line, max);
    }

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
}

using System.Globalization;
using Orbit.Data.Entities;

namespace Orbit.Application.Requests;

/// <summary>What a dependency says about the step waiting on it (§6.20).</summary>
public enum DependencyState
{
    /// <summary>The dependency ended the way the step needs.</summary>
    Satisfied,
    /// <summary>The dependency hasn't settled yet (or failed and may be retried).</summary>
    Waiting,
    /// <summary>The dependency ended another way, or was skipped or cancelled: the step will never start.</summary>
    Never
}

/// <summary>A step of one request as the engine sees it: which flow step, and where it stands.</summary>
public sealed record StepState(Guid FlowStepId, RequestStepStatus Status);

/// <summary>A dependency as the engine sees it.</summary>
public sealed record StepLink(Guid StepId, Guid DependsOnStepId, RequestOutcome? RequiredOutcome);

/// <summary>What one pass of the engine changes: the steps that become Ready, and the ones that are Skipped.</summary>
public sealed record StepTransitions(IReadOnlyList<Guid> Ready, IReadOnlyList<Guid> Skipped);

/// <summary>A person who might be an approver: what the resolution rule needs to know about them.</summary>
public sealed record ApproverCandidate(Guid Id, Guid? DepartmentId, Guid? RoleId, bool IsActive, bool IsSystemAccount);

/// <summary>
/// What the resolution rule needs to know about whoever logged the request: their department, their manager, and the manager of
/// their department - each null when there is none.
/// </summary>
public sealed record ApprovalRequester(Guid? DepartmentId, Guid? ManagerId = null, Guid? DepartmentManagerId = null);

/// <summary>The raw answer to one field as the form posts it: the typed or chosen text, the id picked, or how many files came.</summary>
public sealed record RequestAnswerInput(string? Value, Guid? Id = null, int FileCount = 0);

/// <summary>A cleaned answer: <see cref="Value"/> in its canonical form, <see cref="Id"/> the asset, asset type, project or person picked.</summary>
public sealed record RequestAnswer(RequestFormField Field, string? Value, Guid? Id = null);

/// <summary>
/// What a picker field's scope keeps to by department (§6.20): nothing in particular (<see cref="Kept"/> false), or one department -
/// and no department at all for a requester who has none, which offers nothing.
/// </summary>
public readonly record struct PickerDepartment(bool Kept, Guid? DepartmentId)
{
    /// <summary>Whether the scope offers something of that department. Two missing departments don't match: nothing is nobody's.</summary>
    public bool Offers(Guid? itsDepartmentId) => !Kept || (DepartmentId is Guid d && itsDepartmentId == d);

    /// <summary>Kept to a department the requester doesn't have: there is nothing to offer, and no query to run.</summary>
    public bool OffersNothing => Kept && DepartmentId is null;
}

/// <summary>What a task step creates: the rendered title and description.</summary>
public sealed record ComposedTask(string Title, string? Description);

/// <summary>
/// The request engine's decisions (spec §6.20) as pure functions, so they are unit-tested: when a step may start or will never
/// start, what a request's status is, how an approval stage ends, who its approvers are, how answers are cleaned, and what a task
/// step's task is called and how urgent it is.
/// </summary>
public static class RequestEngineRules
{
    /// <summary>
    /// REQ-015: what a dependency's state means to the step waiting on it. Completed satisfies, unless a declined outcome was
    /// required; Declined satisfies only a step waiting for exactly that; Skipped and Cancelled satisfy nothing, ever; Failed,
    /// Pending and Ready are still open.
    /// </summary>
    public static DependencyState Evaluate(RequestStepStatus dependency, RequestOutcome? required) => (dependency, required) switch
    {
        (RequestStepStatus.Completed, null or RequestOutcome.Accepted) => DependencyState.Satisfied,
        (RequestStepStatus.Completed, _) => DependencyState.Never,
        (RequestStepStatus.Declined, RequestOutcome.Declined) => DependencyState.Satisfied,
        (RequestStepStatus.Declined, _) => DependencyState.Never,
        (RequestStepStatus.Skipped or RequestStepStatus.Cancelled, _) => DependencyState.Never,
        _ => DependencyState.Waiting
    };

    /// <summary>
    /// REQ-015: one pass over a request's steps. A Pending step whose dependencies are all satisfied becomes Ready; one with a
    /// dependency that will never be satisfied is Skipped - and its skipping may skip others, so the pass repeats until nothing
    /// changes. A dependency on a step the request doesn't have (added to the flow after it was logged) is ignored.
    /// </summary>
    public static StepTransitions Next(IReadOnlyList<StepState> steps, IReadOnlyList<StepLink> links)
    {
        var status = steps.ToDictionary(s => s.FlowStepId, s => s.Status);
        var ready = new List<Guid>();
        var skipped = new List<Guid>();
        bool changed;
        do
        {
            changed = false;
            foreach (var step in steps)
            {
                var id = step.FlowStepId;
                if (status[id] != RequestStepStatus.Pending) continue;
                var states = links.Where(l => l.StepId == id && status.ContainsKey(l.DependsOnStepId))
                    .Select(l => Evaluate(status[l.DependsOnStepId], l.RequiredOutcome)).ToList();
                if (states.Contains(DependencyState.Never))
                {
                    status[id] = RequestStepStatus.Skipped;
                    skipped.Add(id);
                    changed = true;
                }
                else if (states.All(s => s == DependencyState.Satisfied))
                {
                    status[id] = RequestStepStatus.Ready;
                    ready.Add(id);
                    changed = true;
                }
            }
        } while (changed);
        return new StepTransitions(ready, skipped);
    }

    /// <summary>
    /// REQ-016: a request's status from its steps'. Cancelled if any step was cancelled; in progress while any step is pending, ready
    /// or failed (a failed step is retried or skipped, by hand); otherwise Declined if an approval was declined, else Completed.
    /// </summary>
    public static RequestStatus Status(IEnumerable<RequestStepStatus> steps)
    {
        var list = steps.ToList();
        if (list.Contains(RequestStepStatus.Cancelled)) return RequestStatus.Cancelled;
        if (list.Any(s => s is RequestStepStatus.Pending or RequestStepStatus.Ready or RequestStepStatus.Failed)) return RequestStatus.InProgress;
        return list.Contains(RequestStepStatus.Declined) ? RequestStatus.Declined : RequestStatus.Completed;
    }

    /// <summary>
    /// REQ-017: how a stage stands from its approvers' decisions. Any decline declines it at once. Otherwise Any needs one approval,
    /// All needs everyone's; null while it is still open. A stage with no decisions at all is open (and shouldn't exist).
    /// </summary>
    public static RequestOutcome? StageOutcome(ApprovalRule rule, IReadOnlyList<ApprovalDecision> decisions)
    {
        if (decisions.Contains(ApprovalDecision.Declined)) return RequestOutcome.Declined;
        if (decisions.Count == 0) return null;
        var approved = rule == ApprovalRule.Any
            ? decisions.Contains(ApprovalDecision.Approved)
            : decisions.All(d => d == ApprovalDecision.Approved);
        return approved ? RequestOutcome.Accepted : null;
    }

    /// <summary>
    /// REQ-018: the people a stage's approvers resolve to when it opens - a named person; everyone holding the role in the department;
    /// everyone holding it in the requester's department (nobody when the requester has none); the requester's manager; the manager
    /// of the requester's department (nobody when there is none). Each person once, active people only, never a system account.
    /// The requester is not excluded: a flow may well ask them to confirm, and a department's manager is asked about their own
    /// request as about anyone's.
    /// </summary>
    public static IReadOnlyList<Guid> ResolveApprovers(IEnumerable<RequestFlowApprover> approvers, ApprovalRequester requester, IReadOnlyList<ApproverCandidate> people)
    {
        var usable = people.Where(p => p.IsActive && !p.IsSystemAccount).ToList();
        var result = new List<Guid>();
        foreach (var a in approvers)
        {
            IEnumerable<Guid> ids = a.Kind switch
            {
                ApproverKind.Person => usable.Where(p => p.Id == a.UserId).Select(p => p.Id),
                ApproverKind.RoleInDepartment => usable.Where(p => p.RoleId == a.RoleId && p.DepartmentId == a.DepartmentId).Select(p => p.Id),
                ApproverKind.RoleInRequestersDepartment => requester.DepartmentId is Guid d
                    ? usable.Where(p => p.RoleId == a.RoleId && p.DepartmentId == d).Select(p => p.Id)
                    : [],
                ApproverKind.RequestersManager => usable.Where(p => p.Id == requester.ManagerId).Select(p => p.Id),
                ApproverKind.RequestersDepartmentManager => usable.Where(p => p.Id == requester.DepartmentManagerId).Select(p => p.Id),
                _ => []
            };
            foreach (var id in ids)
                if (!result.Contains(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// REQ-018: why a stage resolved to nobody and what puts it right, for the manager who reads the failed step: by the kinds of
    /// approver it names - a role nobody active holds, a requester with no manager, a department with none.
    /// </summary>
    public static string NobodyToApprove(IReadOnlyCollection<ApproverKind> kinds, ApprovalRequester requester, string requesterName)
    {
        var why = new List<string>();
        if (kinds.Any(k => k is ApproverKind.Person or ApproverKind.RoleInDepartment or ApproverKind.RoleInRequestersDepartment))
            why.Add("nobody active holds that role there");
        if (kinds.Contains(ApproverKind.RequestersManager))
            why.Add(requester.ManagerId is null ? $"{requesterName} has no manager" : $"{requesterName}'s manager is no longer active");
        if (kinds.Contains(ApproverKind.RequestersDepartmentManager))
            why.Add(requester.DepartmentId is null ? $"{requesterName} isn't in a department"
                : requester.DepartmentManagerId is null ? $"{requesterName}'s department has no manager"
                : $"the manager of {requesterName}'s department is no longer active");
        var managers = kinds.Any(k => k is ApproverKind.RequestersManager or ApproverKind.RequestersDepartmentManager);
        return $"{string.Join("; ", why)}. "
            + (managers ? "Set the manager under Admin > Users or Admin > Departments, or fix the flow's approvers, then retry." : "Fix the flow's approvers, then retry.");
    }

    // ---------------------------------------------------------------- answers

    /// <summary>
    /// REQ-014: every field's answer cleaned, and the refused or missing ones reported per field (by field id). Only the fields given
    /// are checked; an optional field with no answer gives none.
    /// </summary>
    public static (IReadOnlyList<RequestAnswer> Answers, IReadOnlyDictionary<Guid, string> Errors) CleanAnswers(
        IEnumerable<RequestFormField> fields, IReadOnlyDictionary<Guid, RequestAnswerInput> given)
    {
        var answers = new List<RequestAnswer>();
        var errors = new Dictionary<Guid, string>();
        foreach (var f in fields.OrderBy(f => f.DisplayOrder))
        {
            try
            {
                var answer = CleanAnswer(f, given.GetValueOrDefault(f.Id));
                if (answer is not null) answers.Add(answer);
            }
            catch (ValidationException ex)
            {
                errors[f.Id] = ex.Message;
            }
        }
        return (answers, errors);
    }

    /// <summary>
    /// REQ-014: one answer in its canonical form, or null when blank. Text is trimmed with its line breaks as \n; Number an invariant
    /// decimal ("2.5"); Date "yyyy-MM-dd"; Choice one of the choices in its own spelling; Urgency a priority's name (Low, Medium, High
    /// or Critical); Asset, Asset type, Project and User the id picked (the caller checks it is in the field's scope and fills in its
    /// label); Attachment counts its files. A required field refuses a blank.
    /// </summary>
    public static RequestAnswer? CleanAnswer(RequestFormField field, RequestAnswerInput? raw)
    {
        var text = raw?.Value?.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        switch (field.FieldType)
        {
            case RequestFieldType.Attachment:
                if (raw is { FileCount: > 0 }) return new RequestAnswer(field, null);
                if (field.IsRequired) throw new ValidationException("Attach at least one file.");
                return null;
            case RequestFieldType.Asset or RequestFieldType.AssetType or RequestFieldType.Project or RequestFieldType.User:
                if (raw?.Id is Guid id && id != Guid.Empty) return new RequestAnswer(field, null, id);
                if (field.IsRequired)
                    throw new ValidationException(field.FieldType switch
                    {
                        RequestFieldType.Asset => "Choose an asset.",
                        RequestFieldType.AssetType => "Choose an asset type.",
                        RequestFieldType.Project => "Choose a project.",
                        _ => "Choose a person."
                    });
                return null;
        }
        if (string.IsNullOrEmpty(text))
        {
            if (field.IsRequired)
                throw new ValidationException(field.FieldType == RequestFieldType.Urgency ? "Choose how urgent it is." : "This one is required.");
            return null;
        }
        switch (field.FieldType)
        {
            case RequestFieldType.Number:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    return new RequestAnswer(field, number.ToString("0.############################", CultureInfo.InvariantCulture));
                throw new ValidationException("Enter a number, like 3 or 2.5.");
            case RequestFieldType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ValidationException("Enter a date, like 2026-09-30.");
                return new RequestAnswer(field, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case RequestFieldType.Choice:
                var choice = field.Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ValidationException("Choose one of the answers offered.");
                return new RequestAnswer(field, choice);
            case RequestFieldType.Urgency:
                var urgency = ParseUrgency(text) ?? throw new ValidationException("Choose how urgent it is.");
                return new RequestAnswer(field, urgency.ToString());
            default:
                if (text.Length > RequestFlowRules.MaxTextAnswerLength)
                    throw new ValidationException($"The answer must be {RequestFlowRules.MaxTextAnswerLength:N0} characters or fewer.");
                return new RequestAnswer(field, text);
        }
    }

    /// <summary>
    /// REQ-023: the department a picker field's scope keeps what it offers to - the flow's, or the requester's (none when they have
    /// none, so nothing is offered) - and no department for the whole company's and for Held, which goes by holder.
    /// </summary>
    public static PickerDepartment ScopeDepartment(RequestPickerScope? scope, Guid flowDepartmentId, Guid? requesterDepartmentId) => scope switch
    {
        RequestPickerScope.Department => new(true, flowDepartmentId),
        RequestPickerScope.RequestersDepartment => new(true, requesterDepartmentId),
        _ => new(false, null)
    };

    // ---------------------------------------------------------------- tasks

    /// <summary>
    /// REQ-021: the task a task step creates - the title template rendered, its first line cut to the task's limit, or
    /// <paramref name="fallbackTitle"/> when it renders blank; the description template rendered, null when blank.
    /// </summary>
    public static ComposedTask ComposeTask(RequestFlowStep step, Func<string, string?> value, string fallbackTitle)
    {
        var title = RequestFlowRules.FirstLine(RequestTokenRules.Render(step.TitleTemplate, value), RequestFlowRules.MaxTaskTitleLength);
        if (title.Length == 0) title = RequestFlowRules.Cap(fallbackTitle, RequestFlowRules.MaxTaskTitleLength);
        var description = RequestTokenRules.Render(step.DescriptionTemplate, value).Trim();
        return new ComposedTask(title, description.Length == 0 ? null : description);
    }

    /// <summary>
    /// An Urgency answer as a priority: one of the four names, matched ignoring case as a Choice is; null for anything else (so a
    /// "2" isn't quietly taken as Medium).
    /// </summary>
    public static TaskPriority? ParseUrgency(string? value)
    {
        var text = value?.Trim();
        foreach (var priority in Enum.GetValues<TaskPriority>())
            if (string.Equals(priority.ToString(), text, StringComparison.OrdinalIgnoreCase)) return priority;
        return null;
    }

    /// <summary>What each urgency means to the person asking (§6.20): shown beside the choice, in the review and on the request page.</summary>
    public static string UrgencyMeaning(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => "No rush",
        TaskPriority.Medium => "It's slowing me down, but I can work around it",
        TaskPriority.High => "I can't do an important part of my work",
        TaskPriority.Critical => "Work has stopped for me or for several people",
        _ => priority.ToString()
    };

    /// <summary>
    /// REQ-021: the task's priority - the Urgency answer the step's <see cref="RequestFlowStep.PriorityFieldId"/> names, when the
    /// step has one and the answer is a priority; else the step's fixed <see cref="RequestFlowStep.TaskPriority"/>.
    /// </summary>
    public static TaskPriority TaskPriorityFor(RequestFlowStep step, string? urgencyAnswer) =>
        step.PriorityFieldId is null ? step.TaskPriority : ParseUrgency(urgencyAnswer) ?? step.TaskPriority;

    /// <summary>
    /// REQ-021: the task's due date - the day it is created when the step is <see cref="RequestFlowStep.DueOnCreation"/>; else the
    /// Date answer the step's <see cref="RequestFlowStep.DueDateFieldId"/> names, when the step has one and it was answered; else none.
    /// </summary>
    public static DateOnly? TaskDueDateFor(RequestFlowStep step, DateOnly createdOn, string? dateAnswer) =>
        step.DueOnCreation ? createdOn
        : step.DueDateFieldId is not null && DateOnly.TryParseExact(dateAnswer, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : null;

    /// <summary>
    /// The request's title (§6.20): the flow's title and the first line of the first Text answer of the start form ("Product code:
    /// Blue widget"), or "{flow} for {requester}" without one.
    /// </summary>
    public static string RequestTitle(string flowTitle, IEnumerable<RequestAnswer> startAnswers, string requesterName)
    {
        var firstText = startAnswers.FirstOrDefault(a => a.Field.FieldType == RequestFieldType.Text && !string.IsNullOrEmpty(a.Value));
        var line = firstText is null ? string.Empty : RequestFlowRules.FirstLine(firstText.Value!, RequestFlowRules.MaxTitleLineLength);
        return RequestFlowRules.Cap(line.Length > 0 ? $"{flowTitle}: {line}" : $"{flowTitle} for {requesterName}", RequestFlowRules.MaxRequestTitleLength);
    }
}

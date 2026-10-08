using Orbit.Application.Requests;
using Orbit.Data.Entities;

namespace Orbit.Tests.Requests;

/// <summary>The request engine's decisions (spec §6.20): when steps start or are skipped, a request's status, approvals, answers and tasks.</summary>
public class RequestEngineRulesTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static RequestFormField Field(RequestFieldType type, bool required = true, params string[] choices) =>
        new() { Key = type.ToString().ToLowerInvariant(), Prompt = type.ToString(), FieldType = type, IsRequired = required, Choices = [.. choices] };

    /// <summary>REQ-015: what a dependency's state means - the table in §6.20.</summary>
    [Fact]
    public void A_dependency_is_satisfied_waiting_or_never()
    {
        Assert.Equal(DependencyState.Satisfied, RequestEngineRules.Evaluate(RequestStepStatus.Completed, null));
        Assert.Equal(DependencyState.Satisfied, RequestEngineRules.Evaluate(RequestStepStatus.Completed, RequestOutcome.Accepted));
        Assert.Equal(DependencyState.Never, RequestEngineRules.Evaluate(RequestStepStatus.Completed, RequestOutcome.Declined));
        Assert.Equal(DependencyState.Satisfied, RequestEngineRules.Evaluate(RequestStepStatus.Declined, RequestOutcome.Declined));
        Assert.Equal(DependencyState.Never, RequestEngineRules.Evaluate(RequestStepStatus.Declined, null));
        Assert.Equal(DependencyState.Never, RequestEngineRules.Evaluate(RequestStepStatus.Declined, RequestOutcome.Accepted));
        Assert.Equal(DependencyState.Never, RequestEngineRules.Evaluate(RequestStepStatus.Skipped, null));
        Assert.Equal(DependencyState.Never, RequestEngineRules.Evaluate(RequestStepStatus.Cancelled, RequestOutcome.Declined));
        Assert.Equal(DependencyState.Waiting, RequestEngineRules.Evaluate(RequestStepStatus.Pending, null));
        Assert.Equal(DependencyState.Waiting, RequestEngineRules.Evaluate(RequestStepStatus.Ready, RequestOutcome.Accepted));
        Assert.Equal(DependencyState.Waiting, RequestEngineRules.Evaluate(RequestStepStatus.Failed, null));
    }

    /// <summary>REQ-015: a pass starts what may start and skips what never can - skipping cascades; a dependency on a step the request lacks is ignored.</summary>
    [Fact]
    public void A_pass_starts_and_skips_steps()
    {
        StepLink[] links = [new(B, A, null), new(C, B, RequestOutcome.Accepted), new(D, B, RequestOutcome.Declined)];

        // Nothing done yet: only the step with no dependencies starts.
        var t = RequestEngineRules.Next([new(A, RequestStepStatus.Pending), new(B, RequestStepStatus.Pending), new(C, RequestStepStatus.Pending), new(D, RequestStepStatus.Pending)], links);
        Assert.Equal([A], t.Ready);
        Assert.Empty(t.Skipped);

        // The approval was accepted: C starts, D will never.
        t = RequestEngineRules.Next([new(A, RequestStepStatus.Completed), new(B, RequestStepStatus.Completed), new(C, RequestStepStatus.Pending), new(D, RequestStepStatus.Pending)], links);
        Assert.Equal([C], t.Ready);
        Assert.Equal([D], t.Skipped);

        // Declined: the other way round.
        t = RequestEngineRules.Next([new(A, RequestStepStatus.Completed), new(B, RequestStepStatus.Declined), new(C, RequestStepStatus.Pending), new(D, RequestStepStatus.Pending)], links);
        Assert.Equal([D], t.Ready);
        Assert.Equal([C], t.Skipped);

        // A skipped step skips what waits for it, in the same pass.
        var e = Guid.NewGuid();
        t = RequestEngineRules.Next(
            [new(A, RequestStepStatus.Completed), new(B, RequestStepStatus.Declined), new(C, RequestStepStatus.Pending), new(D, RequestStepStatus.Pending), new(e, RequestStepStatus.Pending)],
            [.. links, new(e, C, null)]);
        Assert.Equal(new HashSet<Guid> { C, e }, t.Skipped.ToHashSet());

        // Still waiting: a failed step blocks, it doesn't skip.
        t = RequestEngineRules.Next([new(A, RequestStepStatus.Failed), new(B, RequestStepStatus.Pending)], links);
        Assert.Empty(t.Ready);
        Assert.Empty(t.Skipped);

        // A dependency on a step the request doesn't have (added to the flow later) is ignored.
        t = RequestEngineRules.Next([new(B, RequestStepStatus.Pending)], [new(B, Guid.NewGuid(), null)]);
        Assert.Equal([B], t.Ready);
    }

    /// <summary>REQ-016: a request's status from its steps'.</summary>
    [Fact]
    public void A_requests_status_follows_its_steps()
    {
        Assert.Equal(RequestStatus.InProgress, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Pending]));
        Assert.Equal(RequestStatus.InProgress, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Ready]));
        Assert.Equal(RequestStatus.InProgress, RequestEngineRules.Status([RequestStepStatus.Declined, RequestStepStatus.Failed]));
        Assert.Equal(RequestStatus.Completed, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Skipped]));
        Assert.Equal(RequestStatus.Declined, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Declined, RequestStepStatus.Skipped]));
        Assert.Equal(RequestStatus.Declined, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Declined, RequestStepStatus.Completed]));
        Assert.Equal(RequestStatus.Cancelled, RequestEngineRules.Status([RequestStepStatus.Completed, RequestStepStatus.Cancelled, RequestStepStatus.Pending]));
        Assert.Equal(RequestStatus.Completed, RequestEngineRules.Status([]));
    }

    /// <summary>REQ-017: a stage's outcome - any decline declines; Any needs one approval, All everyone's; open until then.</summary>
    [Fact]
    public void A_stage_ends_by_its_rule()
    {
        Assert.Null(RequestEngineRules.StageOutcome(ApprovalRule.Any, [ApprovalDecision.Pending, ApprovalDecision.Pending]));
        Assert.Equal(RequestOutcome.Accepted, RequestEngineRules.StageOutcome(ApprovalRule.Any, [ApprovalDecision.Approved, ApprovalDecision.Pending]));
        Assert.Null(RequestEngineRules.StageOutcome(ApprovalRule.All, [ApprovalDecision.Approved, ApprovalDecision.Pending]));
        Assert.Equal(RequestOutcome.Accepted, RequestEngineRules.StageOutcome(ApprovalRule.All, [ApprovalDecision.Approved, ApprovalDecision.Approved]));
        Assert.Equal(RequestOutcome.Declined, RequestEngineRules.StageOutcome(ApprovalRule.Any, [ApprovalDecision.Approved, ApprovalDecision.Declined]));
        Assert.Equal(RequestOutcome.Declined, RequestEngineRules.StageOutcome(ApprovalRule.All, [ApprovalDecision.Declined, ApprovalDecision.Pending]));
        Assert.Null(RequestEngineRules.StageOutcome(ApprovalRule.All, []));
    }

    /// <summary>REQ-018: approvers resolve to active people - a person, a role in a department, a role in the requester's department - each once.</summary>
    [Fact]
    public void Approvers_resolve_to_people()
    {
        var finance = Guid.NewGuid();
        var it = Guid.NewGuid();
        var admins = Guid.NewGuid();
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var claude = Guid.NewGuid();
        ApproverCandidate[] people =
        [
            new(ann, finance, admins, true, false), new(ann, finance, null, true, false),
            new(bob, it, admins, true, false), new(bob, it, null, true, false),
            new(cat, finance, null, true, false),
            new(gone, finance, admins, false, false),
            new(claude, null, admins, true, true)
        ];
        RequestFlowApprover[] approvers =
        [
            new() { Kind = ApproverKind.Person, UserId = cat },
            new() { Kind = ApproverKind.RoleInDepartment, RoleId = admins, DepartmentId = it },
            new() { Kind = ApproverKind.RoleInRequestersDepartment, RoleId = admins },
            new() { Kind = ApproverKind.Person, UserId = cat }, // twice is once
            new() { Kind = ApproverKind.Person, UserId = gone }
        ];
        Assert.Equal([cat, bob, ann], RequestEngineRules.ResolveApprovers(approvers, finance, people));
        Assert.Equal([cat, bob], RequestEngineRules.ResolveApprovers(approvers, null, people));
        Assert.Empty(RequestEngineRules.ResolveApprovers([new() { Kind = ApproverKind.RoleInDepartment, RoleId = admins, DepartmentId = Guid.NewGuid() }], finance, people));
    }

    /// <summary>REQ-014: answers in their canonical forms, and refused or missing required answers reported per field.</summary>
    [Fact]
    public void Answers_are_cleaned_and_checked_per_field()
    {
        var text = Field(RequestFieldType.Text);
        var number = Field(RequestFieldType.Number);
        var date = Field(RequestFieldType.Date);
        var choice = Field(RequestFieldType.Choice, true, "Red", "Blue");
        var files = Field(RequestFieldType.Attachment);
        var asset = Field(RequestFieldType.Asset);
        var urgency = Field(RequestFieldType.Urgency);
        var assetType = Field(RequestFieldType.AssetType);
        var optional = Field(RequestFieldType.Text, false);
        var picked = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var (answers, errors) = RequestEngineRules.CleanAnswers([text, number, date, choice, files, asset, urgency, assetType, optional], new Dictionary<Guid, RequestAnswerInput>
        {
            [text.Id] = new(" The laptop\r\nwon't boot "),
            [number.Id] = new("2.50"),
            [date.Id] = new("2026-09-30"),
            [choice.Id] = new("blue"),
            [files.Id] = new(null, null, 2),
            [asset.Id] = new(null, picked),
            [urgency.Id] = new("high"),
            [assetType.Id] = new(null, typeId),
            [optional.Id] = new("  ")
        });
        Assert.Empty(errors);
        Assert.Equal(["The laptop\nwon't boot", "2.5", "2026-09-30", "Blue", null, null, "High", null], answers.Select(a => a.Value));
        Assert.Equal(picked, answers[5].Id);
        Assert.Equal(typeId, answers[7].Id);
        Assert.DoesNotContain(answers, a => a.Field == optional);

        (_, errors) = RequestEngineRules.CleanAnswers([text, number, date, choice, files, asset, urgency, assetType], new Dictionary<Guid, RequestAnswerInput>
        {
            [number.Id] = new("two"),
            [date.Id] = new("30/09/2026"),
            [choice.Id] = new("Green"),
            [asset.Id] = new(null, Guid.Empty),
            [urgency.Id] = new("urgent"),
            [assetType.Id] = new(null, Guid.Empty)
        });
        Assert.Equal(8, errors.Count);
        Assert.Equal("This one is required.", errors[text.Id]);
        Assert.Equal("Attach at least one file.", errors[files.Id]);
        Assert.Equal("Choose an asset.", errors[asset.Id]);
        Assert.Equal("Choose how urgent it is.", errors[urgency.Id]);
        Assert.Equal("Choose an asset type.", errors[assetType.Id]);
        Assert.Contains("number", errors[number.Id]);
        Assert.Contains("date", errors[date.Id]);
        Assert.Contains("offered", errors[choice.Id]);
        Assert.Equal("Choose how urgent it is.", RequestEngineRules.CleanAnswers([urgency], new Dictionary<Guid, RequestAnswerInput>()).Errors[urgency.Id]);
        Assert.Single(RequestEngineRules.CleanAnswers([text], new Dictionary<Guid, RequestAnswerInput> { [text.Id] = new(new string('x', 4001)) }).Errors);
    }

    /// <summary>REQ-021: a task step's title and description rendered from the request's values, the title falling back when it renders blank; the request's own title; the task's priority from an Urgency answer, falling back to the step's.</summary>
    [Fact]
    public void A_task_step_composes_its_task()
    {
        var step = new RequestFlowStep { Kind = RequestStepKind.Task, TitleTemplate = "Code for {{details.description}}\nsecond line", DescriptionTemplate = "Asked by {{request.requester}}.\n{{details.segment}}" };
        var values = new Dictionary<string, string?> { ["details.description"] = "Blue widget", ["request.requester"] = "Ann Lee" };
        var task = RequestEngineRules.ComposeTask(step, k => values.GetValueOrDefault(k), "fallback");
        Assert.Equal("Code for Blue widget", task.Title);
        Assert.Equal("Asked by Ann Lee.", task.Description);

        var blank = RequestEngineRules.ComposeTask(new RequestFlowStep { TitleTemplate = "{{details.segment}}" }, _ => null, "Product code for Ann Lee");
        Assert.Equal("Product code for Ann Lee", blank.Title);
        Assert.Null(blank.Description);
        var longTitle = RequestEngineRules.ComposeTask(new RequestFlowStep { TitleTemplate = new string('t', 400) }, _ => null, "f");
        Assert.Equal(300, longTitle.Title.Length);

        var description = Field(RequestFieldType.Text);
        Assert.Equal("Product code: Blue widget", RequestEngineRules.RequestTitle("Product code", [new RequestAnswer(description, "Blue widget\nmore")], "Ann Lee"));
        Assert.Equal("Product code for Ann Lee", RequestEngineRules.RequestTitle("Product code", [new RequestAnswer(Field(RequestFieldType.Number), "2")], "Ann Lee"));

        var fixedOnly = new RequestFlowStep { Kind = RequestStepKind.Task, TaskPriority = TaskPriority.Low };
        Assert.Equal(TaskPriority.Low, RequestEngineRules.TaskPriorityFor(fixedOnly, "Critical"));
        var fromField = new RequestFlowStep { Kind = RequestStepKind.Task, TaskPriority = TaskPriority.Low, PriorityFieldId = Guid.NewGuid() };
        Assert.Equal(TaskPriority.Critical, RequestEngineRules.TaskPriorityFor(fromField, "Critical"));
        Assert.Equal(TaskPriority.Critical, RequestEngineRules.TaskPriorityFor(fromField, "critical"));
        Assert.Equal(TaskPriority.Low, RequestEngineRules.TaskPriorityFor(fromField, null));
        Assert.Equal(TaskPriority.Low, RequestEngineRules.TaskPriorityFor(fromField, "later"));
        Assert.Null(RequestEngineRules.ParseUrgency("2"));
        Assert.Equal("No rush", RequestEngineRules.UrgencyMeaning(TaskPriority.Low));
        Assert.Equal("Work has stopped for me or for several people", RequestEngineRules.UrgencyMeaning(TaskPriority.Critical));
    }

    /// <summary>REQ-021: a task step's due date - none, the day the task is created, or the answer to the Date field the step names.</summary>
    [Fact]
    public void A_task_step_sets_its_tasks_due_date()
    {
        var created = new DateOnly(2026, 10, 8);

        var none = new RequestFlowStep { Kind = RequestStepKind.Task };
        Assert.Null(RequestEngineRules.TaskDueDateFor(none, created, null));
        Assert.Null(RequestEngineRules.TaskDueDateFor(none, created, "2026-11-30"));

        var onCreation = new RequestFlowStep { Kind = RequestStepKind.Task, DueOnCreation = true };
        Assert.Equal(created, RequestEngineRules.TaskDueDateFor(onCreation, created, null));
        Assert.Equal(created, RequestEngineRules.TaskDueDateFor(onCreation, created, "2026-11-30"));
        onCreation.DueDateFieldId = Guid.NewGuid();
        Assert.Equal(created, RequestEngineRules.TaskDueDateFor(onCreation, created, "2026-11-30"));

        var fromField = new RequestFlowStep { Kind = RequestStepKind.Task, DueDateFieldId = Guid.NewGuid() };
        Assert.Equal(new DateOnly(2026, 11, 30), RequestEngineRules.TaskDueDateFor(fromField, created, "2026-11-30"));
        Assert.Null(RequestEngineRules.TaskDueDateFor(fromField, created, null));
        Assert.Null(RequestEngineRules.TaskDueDateFor(fromField, created, "30/11/2026"));
    }
}

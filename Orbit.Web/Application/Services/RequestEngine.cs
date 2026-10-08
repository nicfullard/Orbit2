using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Jobs;
using Orbit.Scripting;
using Quartz;

namespace Orbit.Application.Services;

/// <summary>
/// What is left to do once the engine's transaction has committed - notifications, the job trigger - so nobody hears of a step that
/// was rolled back. Whoever commits runs it: the engine when the transaction is its own, the caller otherwise.
/// </summary>
public sealed class EngineFollowUp(ILogger logger)
{
    private readonly List<Func<Task>> _actions = [];

    public void Add(Func<Task> action) => _actions.Add(action);

    public async Task RunAsync()
    {
        foreach (var action in _actions)
        {
            try { await action(); }
            catch (Exception ex) { logger.LogWarning(ex, "A follow-up after a request step failed."); }
        }
        _actions.Clear();
    }
}

/// <summary>
/// Walks a request through its flow (spec §6.20). Every change to a request goes through one method here, inside a transaction that
/// locks the request's row, so a task closing and the action job finishing can't trip over each other; whichever is second sees the
/// first's writes. The decisions are <see cref="RequestEngineRules"/>; this class applies them: when a step becomes ready it is
/// started by kind (a form or web page is addressed to its person, a task is created, an approval opens its first stage, an action is
/// left for <see cref="RequestActionJob"/>), and the request's status follows its steps. The actor is explicit: the engine runs for the
/// person who acted, and as <see cref="Actor.System"/> from the job. Nothing here checks permissions - the callers do.
/// </summary>
public sealed class RequestEngine(
    ApplicationDbContext db,
    NumberingService numbering,
    AuditService audit,
    NotificationService notifications,
    ISchedulerFactory schedulers,
    ScriptHost scripts,
    AgentScriptDispatcher agents,
    IOptions<ActionOptions> actionOptions,
    IConfiguration configuration,
    ILogger<RequestEngine> logger)
{
    /// <summary>A request with everything the engine and the request page need: the flow and its steps' definitions, and each step's answers, approvals, task and people.</summary>
    public static IQueryable<Request> WithEverything(IQueryable<Request> q) => q
        .Include(r => r.Flow).ThenInclude(f => f.Category).ThenInclude(c => c.Department)
        .Include(r => r.Requester)
        .Include(r => r.Department)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Fields)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Dependencies)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.User)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.Role)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.Department)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Assignees)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.Action).ThenInclude(a => a!.Parameters)
        .Include(r => r.Steps).ThenInclude(s => s.FlowStep).ThenInclude(fs => fs.ActionInputs)
        .Include(r => r.Steps).ThenInclude(s => s.Answers).ThenInclude(a => a.Field)
        .Include(r => r.Steps).ThenInclude(s => s.Answers).ThenInclude(a => a.Attachment)
        .Include(r => r.Steps).ThenInclude(s => s.Approvals).ThenInclude(a => a.Approver)
        .Include(r => r.Steps).ThenInclude(s => s.Task).ThenInclude(t => t!.Assignments).ThenInclude(x => x.User)
        .Include(r => r.Steps).ThenInclude(s => s.AssignedTo)
        .Include(r => r.Steps).ThenInclude(s => s.CompletedBy)
        .AsSplitQuery();

    // ---------------------------------------------------------------- the events

    /// <summary>
    /// Start whatever may start and settle the request's status. Inside the caller's transaction when there is one (the follow-up is
    /// returned for the caller to run after its commit); otherwise in its own, with the follow-up run here.
    /// </summary>
    public Task<EngineFollowUp> AdvanceAsync(Guid requestId, Actor actor, CancellationToken ct = default) =>
        RunAsync(requestId, actor, (_, _, _) => Task.CompletedTask, ct);

    /// <summary>A form was filled in, or a web page marked done: the step completes and whatever waited for it may start.</summary>
    public Task<EngineFollowUp> CompleteStepAsync(Guid requestId, Guid stepId, Actor actor, CancellationToken ct = default) =>
        RunAsync(requestId, actor, (request, _, _) =>
        {
            var step = request.Steps.First(s => s.Id == stepId);
            if (step.Status != RequestStepStatus.Ready) throw new ValidationException("This step isn't open.");
            var now = DateTime.UtcNow;
            step.Status = RequestStepStatus.Completed;
            step.CompletedAt = now;
            step.CompletedById = actor.UserId;
            audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Updated, request.DepartmentId, request.Title, new { stepCompleted = step.FlowStep.Key });
            return Task.CompletedTask;
        }, ct);

    /// <summary>
    /// A task a task step created was closed (by anyone, anywhere): Done completes the step, Cancelled cancels it. Nothing happens for a
    /// task that isn't a request step's, or whose step already settled - so reopening and closing a task again changes nothing. Never
    /// throws: the task's own save has already happened, and the action job reconciles anything missed.
    /// </summary>
    public async Task OnTaskClosedAsync(TaskItem task, Actor actor, CancellationToken ct = default)
    {
        var hit = await db.RequestSteps.AsNoTracking()
            .Where(s => s.TaskId == task.Id && s.Status == RequestStepStatus.Ready)
            .Select(s => new { s.Id, s.RequestId }).FirstOrDefaultAsync(ct);
        if (hit is null) return;
        try
        {
            var followUp = await RunAsync(hit.RequestId, actor, (request, _, _) =>
            {
                var step = request.Steps.First(s => s.Id == hit.Id);
                if (step.Status != RequestStepStatus.Ready) return Task.CompletedTask;
                var now = DateTime.UtcNow;
                step.Status = task.Status == TaskItemStatus.Done ? RequestStepStatus.Completed : RequestStepStatus.Cancelled;
                step.CompletedAt = now;
                step.CompletedById = actor.UserId;
                audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Updated, request.DepartmentId, request.Title,
                    new { step = step.FlowStep.Key, task = task.Number, taskStatus = task.Status });
                return Task.CompletedTask;
            }, ct);
            await followUp.RunAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Request step for task {Task} could not be settled after the task closed; the action job will retry.", task.Number);
        }
    }

    /// <summary>An approver decides. A decline ends the step at once; an accept closes the stage when its rule is met, opening the next stage or completing the step.</summary>
    public async Task<EngineFollowUp> DecideAsync(Guid approvalId, bool approve, string? comment, Actor actor, CancellationToken ct = default)
    {
        var requestId = await db.RequestApprovals.AsNoTracking().Where(a => a.Id == approvalId).Select(a => (Guid?)a.Step.RequestId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Approval not found.");
        return await RunAsync(requestId, actor, async (request, followUp, ct) =>
        {
            var step = request.Steps.First(s => s.Approvals.Any(a => a.Id == approvalId));
            var approval = step.Approvals.First(a => a.Id == approvalId);
            if (step.Status != RequestStepStatus.Ready) throw new ValidationException("This approval is no longer open.");
            var current = step.Approvals.Max(a => a.StageOrder);
            if (approval.StageOrder != current || approval.Decision != ApprovalDecision.Pending) throw new ValidationException("This approval has already been decided.");

            var now = DateTime.UtcNow;
            approval.Decision = approve ? ApprovalDecision.Approved : ApprovalDecision.Declined;
            approval.Comment = comment;
            approval.DecidedAt = now;
            audit.Add(actor, AuditEntity.Request, request.Id, approve ? AuditAction.Approved : AuditAction.Declined, request.DepartmentId, request.Title,
                new { step = step.FlowStep.Key, stage = current, comment });

            var stage = step.FlowStep.Stages.First(s => s.StageOrder == current);
            var outcome = RequestEngineRules.StageOutcome(stage.Rule, step.Approvals.Where(a => a.StageOrder == current).Select(a => a.Decision).ToList());
            if (outcome == RequestOutcome.Declined)
            {
                step.Status = RequestStepStatus.Declined;
                step.CompletedAt = now;
                step.CompletedById = actor.UserId;
            }
            else if (outcome == RequestOutcome.Accepted && !await OpenStageAsync(request, step, current + 1, actor, now, followUp, ct))
            {
                step.Status = RequestStepStatus.Completed;
                step.CompletedAt = now;
                step.CompletedById = actor.UserId;
            }
        }, ct);
    }

    /// <summary>
    /// Cancel a request in progress: every step still open is cancelled, and so is each open task a task step created (with a comment
    /// saying why). Steps already completed stay as they are.
    /// </summary>
    public Task<EngineFollowUp> CancelAsync(Guid requestId, Actor actor, CancellationToken ct = default) =>
        RunAsync(requestId, actor, (request, _, _) =>
        {
            if (request.Status != RequestStatus.InProgress) throw new ValidationException("This request has already finished.");
            var now = DateTime.UtcNow;
            var cancelled = new List<string>();
            foreach (var step in request.Steps.Where(s => !s.Status.IsSettled()))
            {
                step.Status = RequestStepStatus.Cancelled;
                step.CompletedAt = now;
                step.CompletedById = actor.UserId;
                cancelled.Add(step.FlowStep.Key);
                if (step.Task is { } task && !task.Status.IsClosed())
                {
                    var from = task.Status;
                    task.Status = TaskItemStatus.Cancelled;
                    task.CompletedAt = null;
                    task.UpdatedAt = now;
                    audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.StatusChanged, task.DepartmentId, task.Title,
                        new { status = new { from, to = TaskItemStatus.Cancelled }, request = new { request.Id, request.Number }, reason = "The request was cancelled." });
                    db.Comments.Add(new Comment
                    {
                        TaskId = task.Id,
                        AuthorId = actor.UserId,
                        Body = $"Request {request.Number} was cancelled by {actor.DisplayName}, so this task was cancelled with it.",
                        CreatedAt = now
                    });
                }
            }
            audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Updated, request.DepartmentId, request.Title, new { cancelledSteps = cancelled });
            return Task.CompletedTask;
        }, ct);

    /// <summary>Try a failed step again: an approval re-opens the stage that failed; a task or action step starts afresh.</summary>
    public Task<EngineFollowUp> RetryAsync(Guid requestId, Guid stepId, Actor actor, CancellationToken ct = default) =>
        RunAsync(requestId, actor, async (request, followUp, ct) =>
        {
            var step = request.Steps.First(s => s.Id == stepId);
            if (step.Status != RequestStepStatus.Failed) throw new ValidationException("Only a failed step can be retried.");
            var now = DateTime.UtcNow;
            step.Error = null;
            step.Output = null;
            step.CompletedAt = null;
            audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Retried, request.DepartmentId, request.Title, new { step = step.FlowStep.Key });
            if (step.FlowStep.Kind == RequestStepKind.Approval)
            {
                // The stage that failed is the one after the last that opened; re-open it with the approvers as they stand now.
                var next = step.Approvals.Count == 0 ? 1 : step.Approvals.Max(a => a.StageOrder) + 1;
                step.Status = RequestStepStatus.Ready;
                step.StartedAt ??= now;
                if (!await OpenStageAsync(request, step, next, actor, now, followUp, ct))
                {
                    step.Status = RequestStepStatus.Completed;
                    step.CompletedAt = now;
                }
            }
            else
            {
                step.Status = RequestStepStatus.Pending;
                step.StartedAt = null;
            }
        }, ct);

    /// <summary>Give up on a failed step: it is skipped, and the steps that waited for it will never start.</summary>
    public Task<EngineFollowUp> SkipAsync(Guid requestId, Guid stepId, Actor actor, CancellationToken ct = default) =>
        RunAsync(requestId, actor, (request, _, _) =>
        {
            var step = request.Steps.First(s => s.Id == stepId);
            if (step.Status != RequestStepStatus.Failed) throw new ValidationException("Only a failed step can be skipped.");
            step.Status = RequestStepStatus.Skipped;
            step.CompletedAt = DateTime.UtcNow;
            step.CompletedById = actor.UserId;
            audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Skipped, request.DepartmentId, request.Title, new { step = step.FlowStep.Key, step.Error });
            return Task.CompletedTask;
        }, ct);

    // ---------------------------------------------------------------- actions (the job)

    /// <summary>
    /// What the action job does each time it fires: run every action step that is ready and unclaimed, and settle any step whose
    /// task closed while the hook failed. Returns how many actions ran.
    /// </summary>
    public async Task<int> RunPendingActionsAsync(CancellationToken ct = default)
    {
        var due = await db.RequestSteps.AsNoTracking()
            .Where(s => s.Status == RequestStepStatus.Ready && s.StartedAt == null && s.FlowStep.Kind == RequestStepKind.Action && s.Request.Status == RequestStatus.InProgress)
            .OrderBy(s => s.Request.CreatedAt).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in due)
        {
            try { await RunActionAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Request action step {Step} failed to run.", id); }
        }

        var closed = await db.RequestSteps.AsNoTracking()
            .Where(s => s.Status == RequestStepStatus.Ready && s.TaskId != null && (s.Task!.Status == TaskItemStatus.Done || s.Task.Status == TaskItemStatus.Cancelled))
            .Select(s => s.Task!).ToListAsync(ct);
        foreach (var task in closed) await OnTaskClosedAsync(task, Actor.System, ct);
        return due.Count;
    }

    /// <summary>
    /// Run one action step: claim it (so two job runs never run it twice), run the script outside any lock - on the web server with
    /// the configured connections, or on an agent - then record the outcome and advance. The script runs as <see cref="Actor.System"/>.
    /// </summary>
    public async Task RunActionAsync(Guid stepId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.RequestSteps
            .Where(s => s.Id == stepId && s.Status == RequestStepStatus.Ready && s.StartedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, now), ct);
        if (claimed == 0) return;

        var requestId = await db.RequestSteps.AsNoTracking().Where(s => s.Id == stepId).Select(s => s.RequestId).FirstAsync(ct);
        var request = await WithEverything(db.Requests.AsNoTracking()).FirstAsync(r => r.Id == requestId, ct);
        var step = request.Steps.First(s => s.Id == stepId);
        var flowStep = step.FlowStep;
        var action = flowStep.Action;

        ScriptOutcome outcome;
        if (action is null)
        {
            outcome = new ScriptOutcome(false, null, "The step has no action to run.", 0);
        }
        else
        {
            var values = BuildValues(request);
            var inputs = flowStep.ActionInputs.ToDictionary(i => i.ParameterKey, i => (string?)RequestTokenRules.Render(i.ValueTemplate, values), StringComparer.Ordinal);
            var timeout = Math.Clamp(actionOptions.Value.TimeoutSeconds, 5, 3600);
            logger.LogInformation("Running action \"{Action}\" for request {Number}, step {Step} ({Where}).", action.Name, request.Number, flowStep.Key, action.RunsOn);
            if (action.RunsOn == ActionRunsOn.Web)
            {
                var context = new ScriptRunContext
                {
                    Request = Info(request), Values = values, Inputs = inputs, Connections = WebConnections()
                };
                outcome = await scripts.RunAsync(action.Script, context, TimeSpan.FromSeconds(timeout), ct);
            }
            else
            {
                var result = await agents.RunAsync(action, new ScriptRunRequest
                {
                    Script = action.Script,
                    Inputs = new Dictionary<string, string?>(inputs),
                    Values = new Dictionary<string, string?>(values),
                    Request = new ScriptRequestFacts
                    {
                        Number = request.Number, Title = request.Title, RequesterId = request.RequesterId, RequesterName = request.Requester.DisplayName,
                        RequesterEmail = request.Requester.Email, Department = request.Department.Name, Category = request.Flow.Category.Title, Flow = request.Flow.Title
                    }
                }, ct);
                outcome = result is null
                    ? new ScriptOutcome(false, null, agents.Unavailable(action), 0)
                    : new ScriptOutcome(result.Ok, result.Output, result.Error, result.DurationMs);
            }
        }

        var followUp = await RunAsync(request.Id, Actor.System, (r, _, _) =>
        {
            var s = r.Steps.First(x => x.Id == stepId);
            if (s.Status != RequestStepStatus.Ready) return Task.CompletedTask; // cancelled while the script ran
            var finished = DateTime.UtcNow;
            s.Output = outcome.Output;
            if (outcome.Ok)
            {
                s.Status = RequestStepStatus.Completed;
                s.CompletedAt = finished;
                s.Error = null;
            }
            else
            {
                s.Status = RequestStepStatus.Failed;
                s.Error = outcome.Error is { Length: > 4000 } e ? e[..4000] : outcome.Error;
                s.CompletedAt = null;
            }
            audit.Add(Actor.System, AuditEntity.Request, r.Id, AuditAction.Updated, r.DepartmentId, r.Title,
                new { step = s.FlowStep.Key, action = action?.Name, ok = outcome.Ok, outcome.DurationMs, error = s.Error });
            return Task.CompletedTask;
        }, ct);
        await followUp.RunAsync();
    }

    // ---------------------------------------------------------------- the pass

    private async Task<EngineFollowUp> RunAsync(Guid requestId, Actor actor, Func<Request, EngineFollowUp, CancellationToken, Task> work, CancellationToken ct)
    {
        var followUp = new EngineFollowUp(logger);
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;

        // The row lock: anything else touching this request waits until this transaction ends, then sees its writes.
        var locked = await db.Requests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, DateTime.UtcNow), ct);
        if (locked == 0) throw new NotFoundException("Request not found.");
        var request = await WithEverything(db.Requests).FirstAsync(r => r.Id == requestId, ct);

        await work(request, followUp, ct);
        await AdvanceCoreAsync(request, actor, followUp, ct);
        await db.SaveChangesAsync(ct);

        if (tx is null) return followUp;
        await tx.CommitAsync(ct);
        await followUp.RunAsync();
        return followUp;
    }

    private async Task AdvanceCoreAsync(Request request, Actor actor, EngineFollowUp followUp, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var steps = request.Steps.OrderBy(s => s.FlowStep.DisplayOrder).ToList();
        var states = steps.Select(s => new StepState(s.FlowStepId, s.Status)).ToList();
        var links = steps.SelectMany(s => s.FlowStep.Dependencies).Select(d => new StepLink(d.StepId, d.DependsOnStepId, d.RequiredOutcome)).ToList();
        var transitions = RequestEngineRules.Next(states, links);

        foreach (var id in transitions.Skipped)
        {
            var step = steps.First(s => s.FlowStepId == id);
            step.Status = RequestStepStatus.Skipped;
            step.CompletedAt = now;
        }
        foreach (var id in transitions.Ready)
            await StartStepAsync(request, steps.First(s => s.FlowStepId == id), actor, now, followUp, ct);

        request.UpdatedAt = now;
        var status = RequestEngineRules.Status(request.Steps.Select(s => s.Status));
        if (status == request.Status) return;
        var from = request.Status;
        request.Status = status;
        request.CompletedAt = status == RequestStatus.InProgress ? null : now;
        audit.Add(actor, AuditEntity.Request, request.Id, status switch
        {
            RequestStatus.Completed => AuditAction.Completed,
            RequestStatus.Declined => AuditAction.Declined,
            RequestStatus.Cancelled => AuditAction.Cancelled,
            _ => AuditAction.StatusChanged
        }, request.DepartmentId, request.Title, new { status = new { from, to = status } });
        if (status != RequestStatus.InProgress && actor.UserId != request.RequesterId)
            followUp.Add(() => notifications.RequestFinishedAsync(request, request.Requester));
    }

    private async Task StartStepAsync(Request request, RequestStep step, Actor actor, DateTime now, EngineFollowUp followUp, CancellationToken ct)
    {
        var flowStep = step.FlowStep;
        step.StartedAt = now;
        switch (flowStep.Kind)
        {
            case RequestStepKind.Form:
            case RequestStepKind.Url:
                step.AssignedToId = flowStep.PerformedById ?? request.RequesterId;
                step.Status = RequestStepStatus.Ready;
                if (step.AssignedToId != actor.UserId)
                    Notify(followUp, step.AssignedToId.Value, request, flowStep.Kind == RequestStepKind.Form ? $"fill in \"{flowStep.Title}\"" : $"open \"{flowStep.Title}\"");
                break;
            case RequestStepKind.Task:
                await CreateTaskAsync(request, step, actor, now, followUp, ct);
                break;
            case RequestStepKind.Approval:
                step.Status = RequestStepStatus.Ready;
                await OpenStageAsync(request, step, 1, actor, now, followUp, ct);
                break;
            case RequestStepKind.Action:
                // Claimed and run by the job, outside this transaction; StartedAt marks the claim.
                step.Status = RequestStepStatus.Ready;
                step.StartedAt = null;
                followUp.Add(TriggerJobAsync);
                break;
        }
    }

    private async Task CreateTaskAsync(Request request, RequestStep step, Actor actor, DateTime now, EngineFollowUp followUp, CancellationToken ct)
    {
        var flowStep = step.FlowStep;
        var departmentId = flowStep.TaskDepartmentId ?? request.DepartmentId;
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        if (department is null || department.IsArchived)
        {
            Fail(request, step, actor, $"The task can't be created: the department it is filed in{(department is null ? "" : $" (\"{department.Name}\")")} {(department is null ? "no longer exists" : "is archived")}. Change the step's department, then retry.");
            return;
        }

        var values = BuildValues(request);
        var composed = RequestEngineRules.ComposeTask(flowStep, k => values.GetValueOrDefault(k), $"{request.Flow.Title} for {request.Requester.DisplayName}");
        var answers = request.Steps.SelectMany(s => s.Answers).ToList();
        RequestFormAnswer? Answer(Guid? fieldId) => fieldId is Guid id ? answers.FirstOrDefault(a => a.FieldId == id) : null;

        DateOnly? due = Answer(flowStep.DueDateFieldId)?.Value is string dv
            && DateOnly.TryParseExact(dv, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        // A disposed asset, an archived project or an inactive person is dropped quietly, as the recurring generator does (§6.19).
        var assetId = Answer(flowStep.AssetFieldId)?.AssetId;
        if (assetId is Guid aid && !await db.Assets.AnyAsync(a => a.Id == aid && a.Status != AssetStatus.Disposed, ct)) assetId = null;
        var projectId = Answer(flowStep.ProjectFieldId)?.ProjectId;
        if (projectId is Guid pid && !await db.Projects.AnyAsync(p => p.Id == pid && p.Status != ProjectStatus.Archived, ct)) projectId = null;
        Guid? requesteeId = flowStep.RequesteeSource switch
        {
            RequesteeSource.Requester => request.RequesterId,
            RequesteeSource.Field => Answer(flowStep.RequesteeFieldId)?.UserId,
            _ => null
        };
        if (requesteeId is Guid rid && !await db.Users.AnyAsync(u => u.Id == rid && u.IsActive && !u.IsSystemAccount, ct)) requesteeId = null;
        var wanted = flowStep.Assignees.Select(a => a.UserId).ToList();
        var assigneeIds = wanted.Count == 0 ? [] : await db.Users.Where(u => wanted.Contains(u.Id) && u.IsActive && !u.IsSystemAccount).Select(u => u.Id).ToListAsync(ct);

        var task = new TaskItem
        {
            Number = await numbering.NextAsync(NumberingService.TaskPrefix, now, ct),
            Title = composed.Title,
            Description = composed.Description,
            DepartmentId = departmentId,
            ProjectId = projectId,
            AssetId = assetId,
            Priority = RequestEngineRules.TaskPriorityFor(flowStep, Answer(flowStep.PriorityFieldId)?.Value),
            Type = flowStep.TaskType,
            DueDate = due,
            Status = TaskItemStatus.Todo,
            Source = TaskSource.Request,
            CreatedById = request.RequesterId,
            RequesteeId = requesteeId,
            Assignments = [],
            CreatedAt = now,
            UpdatedAt = now
        };
        // The flow assigned them, not a person: AssignedById stays null.
        foreach (var userId in assigneeIds)
            task.Assignments.Add(new TaskAssignment { TaskId = task.Id, UserId = userId, AssignedAt = now });
        db.Tasks.Add(task);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Created, departmentId, task.Title, new
        {
            task.Number, task.Title, task.Status, task.Priority, task.Type, task.ProjectId, task.DepartmentId, task.AssigneeIds,
            task.DueDate, task.Source, task.AssetId, task.RequesteeId, request = new { id = request.Id, number = request.Number, step = flowStep.Key }
        });
        step.TaskId = task.Id;
        step.Task = task;
        step.Status = RequestStepStatus.Ready;

        // The task row must exist before files are copied to it; the copy never loads the bytes.
        await db.SaveChangesAsync(ct);
        await CopyAttachmentsAsync(request, flowStep, task, ct);

        var tell = assigneeIds.ToList();
        if (requesteeId is Guid r && r != request.RequesterId || requesteeId is Guid r2 && actor.UserId != r2) tell.Add(requesteeId!.Value);
        followUp.Add(async () =>
        {
            var people = await db.Users.AsNoTracking().Where(u => tell.Contains(u.Id)).ToListAsync(CancellationToken.None);
            foreach (var person in people)
            {
                if (assigneeIds.Contains(person.Id)) await notifications.TaskAssignedAsync(task, person, actor);
                else if (person.Id != actor.UserId) await notifications.TaskCreatedForAsync(task, person, actor);
            }
        });
    }

    private async Task CopyAttachmentsAsync(Request request, RequestFlowStep flowStep, TaskItem task, CancellationToken ct)
    {
        IEnumerable<RequestStep> sources = flowStep.CopyAllAttachments
            ? request.Steps.Where(s => s.FlowStep.Kind == RequestStepKind.Form)
            : flowStep.CopyAttachmentsFromStepId is Guid from ? request.Steps.Where(s => s.FlowStepId == from) : [];
        var ids = sources.SelectMany(s => s.Answers).Where(a => a.AttachmentId is not null).Select(a => a.AttachmentId!.Value).Distinct().ToList();
        foreach (var source in ids)
        {
            var copy = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Attachments" ("Id", "TaskId", "FileName", "ContentType", "SizeBytes", "UploadedById", "UploadedAt")
                SELECT {copy}, {task.Id}, a."FileName", a."ContentType", a."SizeBytes", a."UploadedById", a."UploadedAt"
                FROM "Attachments" a WHERE a."Id" = {source}
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AttachmentContents" ("AttachmentId", "Data")
                SELECT {copy}, c."Data" FROM "AttachmentContents" c WHERE c."AttachmentId" = {source}
                """, ct);
        }
    }

    /// <summary>Open a stage: its approvers resolved to people as they stand now, one Pending decision each. False when there is no such stage.</summary>
    private async Task<bool> OpenStageAsync(Request request, RequestStep step, int order, Actor actor, DateTime now, EngineFollowUp followUp, CancellationToken ct)
    {
        var stage = step.FlowStep.Stages.FirstOrDefault(s => s.StageOrder == order);
        if (stage is null) return false;
        var candidates = await CandidatesAsync(stage.Approvers, ct);
        var ids = RequestEngineRules.ResolveApprovers(stage.Approvers, request.Requester.DepartmentId, candidates);
        if (ids.Count == 0)
        {
            var named = string.Join("; ", stage.Approvers.Select(RequestCatalogueService.ApproverLabel));
            Fail(request, step, actor, $"Stage {order} of \"{step.FlowStep.Title}\" has nobody to approve it ({named}): nobody active holds that role there. Fix the flow's approvers, then retry.");
            return true;
        }
        foreach (var id in ids)
        {
            var approval = new RequestApproval { StepId = step.Id, Step = step, StageOrder = order, ApproverId = id };
            step.Approvals.Add(approval);
            db.RequestApprovals.Add(approval);
            Notify(followUp, id, request, $"approve \"{step.FlowStep.Title}\"");
        }
        return true;
    }

    private async Task<IReadOnlyList<ApproverCandidate>> CandidatesAsync(IEnumerable<RequestFlowApprover> approvers, CancellationToken ct)
    {
        var userIds = approvers.Where(a => a.Kind == ApproverKind.Person && a.UserId != null).Select(a => a.UserId!.Value).ToList();
        var roleIds = approvers.Where(a => a.Kind != ApproverKind.Person && a.RoleId != null).Select(a => a.RoleId!.Value).ToList();
        var rows = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) || db.UserRoles.Any(ur => ur.UserId == u.Id && roleIds.Contains(ur.RoleId)))
            .Select(u => new { u.Id, u.DepartmentId, u.IsActive, u.IsSystemAccount, Roles = db.UserRoles.Where(ur => ur.UserId == u.Id).Select(ur => ur.RoleId).ToList() })
            .ToListAsync(ct);
        return rows.SelectMany(r => r.Roles
                .Select(role => new ApproverCandidate(r.Id, r.DepartmentId, role, r.IsActive, r.IsSystemAccount))
                .Append(new ApproverCandidate(r.Id, r.DepartmentId, null, r.IsActive, r.IsSystemAccount)))
            .ToList();
    }

    private void Fail(Request request, RequestStep step, Actor actor, string error)
    {
        step.Status = RequestStepStatus.Failed;
        step.Error = error.Length > 4000 ? error[..4000] : error;
        step.CompletedAt = null;
        audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Updated, request.DepartmentId, request.Title, new { stepFailed = step.FlowStep.Key, error = step.Error });
    }

    private void Notify(EngineFollowUp followUp, Guid userId, Request request, string what) =>
        followUp.Add(async () =>
        {
            var person = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, CancellationToken.None);
            if (person is not null) await notifications.RequestStepAsync(request, person, what);
        });

    private async Task TriggerJobAsync()
    {
        try
        {
            var scheduler = await schedulers.GetScheduler();
            if (await scheduler.GetJobDetail(RequestActionJob.Key) is not null) await scheduler.TriggerJob(RequestActionJob.Key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't trigger the request action job; its schedule will pick the step up.");
        }
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// Every token's value for a request as it stands (§6.20): the request's own, each form's answers by "step.field" (a picked
    /// asset, project or person by its label; files by name), a task step's number, title, assignees and completion, an approval's
    /// outcome and comments, an action's output. Needs the request loaded with <see cref="WithEverything"/>.
    /// </summary>
    public static Dictionary<string, string?> BuildValues(Request request)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["request.number"] = request.Number,
            ["request.title"] = request.Title,
            ["request.requester"] = request.Requester.DisplayName,
            ["request.requester.email"] = request.Requester.Email,
            ["request.department"] = request.Department.Name,
            ["request.category"] = request.Flow.Category.Title,
            ["request.flow"] = request.Flow.Title
        };
        foreach (var step in request.Steps)
        {
            var key = step.FlowStep.Key;
            switch (step.FlowStep.Kind)
            {
                case RequestStepKind.Form:
                    foreach (var field in step.FlowStep.Fields)
                    {
                        var answers = step.Answers.Where(a => a.FieldId == field.Id).ToList();
                        values[$"{key}.{field.Key}"] = answers.Count == 0 ? null
                            : field.FieldType == RequestFieldType.Attachment ? string.Join(", ", answers.Select(a => a.Value))
                            : answers[0].Value;
                    }
                    break;
                case RequestStepKind.Task:
                    values[$"{key}.number"] = step.Task?.Number;
                    values[$"{key}.title"] = step.Task?.Title;
                    values[$"{key}.assignees"] = step.Task is null ? null : string.Join(", ", step.Task.Assignments.Select(a => a.User.DisplayName));
                    values[$"{key}.completed-by"] = step.Status == RequestStepStatus.Completed ? step.CompletedBy?.DisplayName : null;
                    values[$"{key}.completed-on"] = step.Status == RequestStepStatus.Completed ? step.CompletedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                    break;
                case RequestStepKind.Approval:
                    values[$"{key}.outcome"] = step.Status switch
                    {
                        RequestStepStatus.Completed => RequestOutcome.Accepted.ToString(),
                        RequestStepStatus.Declined => RequestOutcome.Declined.ToString(),
                        _ => null
                    };
                    var comments = step.Approvals.Where(a => !string.IsNullOrWhiteSpace(a.Comment)).OrderBy(a => a.StageOrder).ThenBy(a => a.DecidedAt)
                        .Select(a => $"{a.Approver.DisplayName}: {a.Comment}").ToList();
                    values[$"{key}.comments"] = comments.Count == 0 ? null : string.Join("\n", comments);
                    break;
                case RequestStepKind.Action:
                    values[$"{key}.output"] = step.Output;
                    break;
            }
        }
        return values;
    }

    private static ScriptRequestInfo Info(Request request) => new()
    {
        Number = request.Number,
        Title = request.Title,
        Requester = new ScriptRequester { Id = request.RequesterId, Name = request.Requester.DisplayName, Email = request.Requester.Email },
        Department = request.Department.Name,
        Category = request.Flow.Category.Title,
        Flow = request.Flow.Title
    };

    /// <summary>The connections a web-side script may open: the configured ones, and Orbit's own database as "orbit".</summary>
    private IReadOnlyDictionary<string, DbConnectionSpec> WebConnections()
    {
        var specs = new Dictionary<string, DbConnectionSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, c) in actionOptions.Value.Connections)
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(c.ConnectionString))
                specs[name.Trim()] = new DbConnectionSpec(c.Provider, c.ConnectionString);
        if (configuration.GetConnectionString("DefaultConnection") is { Length: > 0 } orbit)
            specs[ActionOptions.OrbitConnection] = new DbConnectionSpec("postgres", orbit);
        return specs;
    }
}

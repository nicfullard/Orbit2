using Microsoft.EntityFrameworkCore;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>One match for a form's Asset, Project or Person field (§6.20), shaped for the pickers.</summary>
public sealed record RequestLookupItem(Guid Id, string Name, string? Tag, string? Detail);

/// <summary>
/// Requests as the people who log them and act on them see them (spec §6.20): the catalogue of offered flows, logging a request, the
/// steps addressed to a person (a form to fill in, a page to open, an approval to give), the lists, and a manager's cancel, retry and
/// skip. Permissions are checked here; the <see cref="RequestEngine"/> then moves the request along.
/// </summary>
public sealed class RequestService(
    ApplicationDbContext db,
    IActorProvider actors,
    AttachmentService attachments,
    RequestEngine engine,
    NumberingService numbering,
    AuditService audit)
{
    // ---------------------------------------------------------------- the catalogue

    /// <summary>Every department's live categories, each with its live flows in order, grouped by department name.</summary>
    public async Task<IReadOnlyList<RequestCatalogueSection>> CatalogueAsync(CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        var flows = (await OfferedFlows().ToListAsync(ct)).Where(RequestFlowRules.IsLive).ToList();
        var categories = flows.GroupBy(f => f.CategoryId).Select(g =>
        {
            var category = g.First().Category;
            category.Flows = g.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Title).ToList();
            return category;
        }).ToList();
        return categories.GroupBy(c => c.DepartmentId)
            .Select(g => new RequestCatalogueSection(g.First().Department, g.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Title).ToList()))
            .OrderBy(s => s.Department.Name)
            .ToList();
    }

    /// <summary>A live category with its live flows, for the Requests page's second step.</summary>
    public async Task<RequestCategory> CategoryAsync(Guid id, CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        var flows = (await OfferedFlows().Where(f => f.CategoryId == id).ToListAsync(ct)).Where(RequestFlowRules.IsLive).ToList();
        if (flows.Count == 0) throw new NotFoundException("That request category isn't offered at the moment.");
        var category = flows[0].Category;
        category.Flows = flows.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Title).ToList();
        return category;
    }

    /// <summary>A live flow with its steps and the start form's fields, for the page that logs a request through it.</summary>
    public async Task<RequestFlow> FlowAsync(Guid flowId, CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        var flow = await OfferedFlows().FirstOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null || !RequestFlowRules.IsLive(flow)) throw new NotFoundException("That request isn't offered at the moment.");
        Order(flow);
        return flow;
    }

    // ---------------------------------------------------------------- logging and acting

    /// <summary>
    /// Log a request: check the start form's answers (<see cref="RequestAnswersException"/> names each one refused) and files, then in
    /// one transaction create the request with a step per flow step, write the answers and files, mark the start form done and let
    /// the engine start whatever follows. A repeated post with the same idempotency key returns the request already logged.
    /// </summary>
    public async Task<Request> StartAsync(
        Guid flowId, IReadOnlyDictionary<Guid, RequestAnswerInput> answers, IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentUpload>> files,
        string? idempotencyKey, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var me = actor.UserId!.Value;
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is not null)
        {
            var done = await db.Requests.AsNoTracking().Include(r => r.Flow).Include(r => r.Department)
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key && r.RequesterId == me, ct);
            if (done is not null) return done;
        }

        var flow = await FlowAsync(flowId, ct);
        var startForm = RequestFlowRules.StartForm(flow);
        var (clean, labels) = startForm is null
            ? ([], new Dictionary<Guid, Pick>())
            : await CleanFormAsync(flow, startForm, answers, files, actor.DepartmentId, ct);
        attachments.CheckUploads(files.Values.SelectMany(f => f).ToList());

        var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me, ct);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = new Request
        {
            Number = await numbering.NextAsync(NumberingService.RequestPrefix, now, ct),
            FlowId = flow.Id,
            DepartmentId = flow.Category.DepartmentId,
            Title = RequestEngineRules.RequestTitle(flow.Title, clean, requester.DisplayName),
            RequesterId = me,
            Status = RequestStatus.InProgress,
            IdempotencyKey = key,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var flowStep in flow.Steps)
        {
            var step = new RequestStep { RequestId = request.Id, FlowStepId = flowStep.Id, Status = RequestStepStatus.Pending };
            if (flowStep.Id == startForm?.Id)
            {
                step.Status = RequestStepStatus.Completed;
                step.AssignedToId = me;
                step.StartedAt = now;
                step.CompletedAt = now;
                step.CompletedById = me;
            }
            request.Steps.Add(step);
        }
        db.Requests.Add(request);
        audit.Add(actor, AuditEntity.Request, request.Id, AuditAction.Created, request.DepartmentId, request.Title,
            new { request.Number, flow = flow.Title, flowId = flow.Id, category = flow.Category.Title, department = flow.Category.Department.Name });
        await db.SaveChangesAsync(ct);
        if (startForm is not null)
            await WriteAnswersAsync(actor, request, request.Steps.First(s => s.FlowStepId == startForm.Id), startForm.Fields, clean, labels, files, ct);
        var followUp = await engine.AdvanceAsync(request.Id, actor, ct);
        await tx.CommitAsync(ct);
        await followUp.RunAsync();
        return request;
    }

    /// <summary>A form step addressed to the caller, for the page that fills it in: the step, its fields, and its request.</summary>
    public async Task<RequestStep> FormStepAsync(Guid stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var step = await db.RequestSteps.AsNoTracking()
            .Include(s => s.FlowStep).ThenInclude(fs => fs.Fields)
            .Include(s => s.FlowStep).ThenInclude(fs => fs.Flow).ThenInclude(f => f.Category).ThenInclude(c => c.Department)
            .Include(s => s.Request).ThenInclude(r => r.Requester)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct) ?? throw new NotFoundException("Request step not found.");
        AccessPolicy.Require(AccessPolicy.CanActOnStep(actor, step.Request, step), "This step isn't addressed to you.");
        if (step.FlowStep.Kind != RequestStepKind.Form) throw new NotFoundException("This step isn't a form.");
        if (step.Status != RequestStepStatus.Ready) throw new ValidationException("This form isn't open.");
        step.FlowStep.Fields = step.FlowStep.Fields.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Prompt).ToList();
        return step;
    }

    /// <summary>Fill in a later form step: the answers and files are checked as when logging, written, and the step completed.</summary>
    public async Task SubmitFormAsync(
        Guid stepId, IReadOnlyDictionary<Guid, RequestAnswerInput> answers, IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentUpload>> files,
        CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var step = await db.RequestSteps
            .Include(s => s.FlowStep).ThenInclude(fs => fs.Fields)
            .Include(s => s.FlowStep).ThenInclude(fs => fs.Flow).ThenInclude(f => f.Category)
            .Include(s => s.Request).ThenInclude(r => r.Requester)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct) ?? throw new NotFoundException("Request step not found.");
        AccessPolicy.Require(AccessPolicy.CanActOnStep(actor, step.Request, step), "This step isn't addressed to you.");
        if (step.FlowStep.Kind != RequestStepKind.Form) throw new ValidationException("This step isn't a form.");
        if (step.Status != RequestStepStatus.Ready) throw new ValidationException("This form isn't open.");
        var (clean, labels) = await CleanFormAsync(step.FlowStep.Flow, step.FlowStep, answers, files, step.Request.Requester.DepartmentId, ct);
        attachments.CheckUploads(files.Values.SelectMany(f => f).ToList());

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WriteAnswersAsync(actor, step.Request, step, step.FlowStep.Fields, clean, labels, files, ct);
        var followUp = await engine.CompleteStepAsync(step.RequestId, step.Id, actor, ct);
        await tx.CommitAsync(ct);
        await followUp.RunAsync();
    }

    /// <summary>A web-page step was opened: mark it done.</summary>
    public async Task MarkUrlDoneAsync(Guid stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var step = await db.RequestSteps.AsNoTracking().Include(s => s.FlowStep).Include(s => s.Request).FirstOrDefaultAsync(s => s.Id == stepId, ct)
            ?? throw new NotFoundException("Request step not found.");
        AccessPolicy.Require(AccessPolicy.CanActOnStep(actor, step.Request, step), "This step isn't addressed to you.");
        if (step.FlowStep.Kind != RequestStepKind.Url) throw new ValidationException("This step isn't a web page.");
        if (step.Status != RequestStepStatus.Ready) throw new ValidationException("This step isn't open.");
        await engine.CompleteStepAsync(step.RequestId, step.Id, actor, ct);
    }

    /// <summary>The web-page step's address with its tokens filled in, for the person opening it.</summary>
    public async Task<string> UrlAsync(Guid stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var requestId = await db.RequestSteps.AsNoTracking().Where(s => s.Id == stepId).Select(s => (Guid?)s.RequestId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Request step not found.");
        var request = await GetAsync(requestId, ct);
        var step = request.Steps.First(s => s.Id == stepId);
        if (step.FlowStep.Kind != RequestStepKind.Url) throw new NotFoundException("This step isn't a web page.");
        return RequestTokenRules.RenderUrl(step.FlowStep.Url, RequestEngine.BuildValues(request));
    }

    /// <summary>Approve or decline, with a comment: only the approver it was asked of.</summary>
    public async Task DecideAsync(Guid approvalId, bool approve, string? comment, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var approval = await db.RequestApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == approvalId, ct)
            ?? throw new NotFoundException("Approval not found.");
        AccessPolicy.Require(AccessPolicy.CanDecide(actor, approval), "This approval wasn't asked of you.");
        var text = RequestFlowRules.Clean(comment, RequestFlowRules.MaxCommentLength, "The comment");
        await engine.DecideAsync(approvalId, approve, text, actor, ct);
    }

    public async Task CancelAsync(Guid requestId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new NotFoundException("Request not found.");
        AccessPolicy.Require(AccessPolicy.CanCancelRequest(actor, request), "Only the person who logged a request, or a manager of its department, can cancel it while it is in progress.");
        await engine.CancelAsync(requestId, actor, ct);
    }

    public async Task RetryAsync(Guid stepId, CancellationToken ct = default)
    {
        var (actor, step) = await ManagedStepAsync(stepId, ct);
        await engine.RetryAsync(step.RequestId, step.Id, actor, ct);
    }

    public async Task SkipAsync(Guid stepId, CancellationToken ct = default)
    {
        var (actor, step) = await ManagedStepAsync(stepId, ct);
        await engine.SkipAsync(step.RequestId, step.Id, actor, ct);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>A request with everything on it, for its page: its requester, anyone it was addressed to, or a manager of its department.</summary>
    public async Task<Request> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var request = await RequestEngine.WithEverything(db.Requests.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Request not found.");
        var me = actor.UserId;
        var participant = request.Steps.Any(s => s.AssignedToId == me) || request.Steps.Any(s => s.Approvals.Any(a => a.ApproverId == me));
        AccessPolicy.Require(AccessPolicy.CanViewRequest(actor, request, participant), "You don't have permission to see this request.");
        request.Steps = request.Steps.OrderBy(s => s.FlowStep.DisplayOrder).ThenBy(s => s.FlowStep.Title).ToList();
        foreach (var s in request.Steps) s.FlowStep.Fields = s.FlowStep.Fields.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Prompt).ToList();
        return request;
    }

    /// <summary>
    /// The requests the caller follows: the ones they logged or were addressed in - and, with requests.submit at Department, the ones
    /// anyone in their department logged. Open ones first, then the newest, at most <paramref name="take"/>.
    /// </summary>
    public async Task<IReadOnlyList<Request>> MyRequestsAsync(int take = 20, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var me = actor.UserId;
        var dept = actor.DepartmentId;
        var department = AccessPolicy.CanSeeDepartmentsRequests(actor) && dept is not null;
        return await db.Requests.AsNoTracking().Include(r => r.Flow).Include(r => r.Department).Include(r => r.Requester)
            .Where(r => r.RequesterId == me
                || r.Steps.Any(s => s.AssignedToId == me)
                || r.Steps.Any(s => s.Approvals.Any(a => a.ApproverId == me))
                || (department && r.Requester.DepartmentId == dept))
            .OrderByDescending(r => r.Status == RequestStatus.InProgress).ThenByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
    }

    /// <summary>The steps waiting for the caller: forms to fill in, pages to open, approvals to give - oldest first.</summary>
    public async Task<IReadOnlyList<RequestActionItem>> NeedsMyActionAsync(int take = 50, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var me = actor.UserId;
        var steps = await db.RequestSteps.AsNoTracking()
            .Include(s => s.FlowStep)
            .Include(s => s.Request).ThenInclude(r => r.Flow)
            .Include(s => s.Request).ThenInclude(r => r.Department)
            .Include(s => s.Request).ThenInclude(r => r.Requester)
            .Where(s => s.Status == RequestStepStatus.Ready && s.AssignedToId == me && s.Request.Status == RequestStatus.InProgress
                && (s.FlowStep.Kind == RequestStepKind.Form || s.FlowStep.Kind == RequestStepKind.Url))
            .ToListAsync(ct);
        var approvals = await OpenApprovalsFor(me).ToListAsync(ct);
        return steps.Select(s => new RequestActionItem(s.Request, s,
                    s.FlowStep.Kind == RequestStepKind.Form ? $"Fill in \"{s.FlowStep.Title}\"" : $"Open \"{s.FlowStep.Title}\"", s.StartedAt ?? s.Request.CreatedAt))
            .Concat(approvals.Select(ApprovalItem))
            .OrderBy(i => i.Since)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
    }

    /// <summary>
    /// The approvals waiting for the caller (§6.9, §6.20), oldest first, for the dashboard's My waiting approvals card. Null when
    /// their role can't open requests (no requests.submit) - the request page is behind that door, so the card isn't shown - and
    /// never a refusal, since the dashboard is everyone's.
    /// </summary>
    public async Task<IReadOnlyList<RequestActionItem>?> WaitingApprovalsAsync(int take = 50, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (!AccessPolicy.CanSubmitRequests(actor)) return null;
        var approvals = await OpenApprovalsFor(actor.UserId).ToListAsync(ct);
        return approvals.Select(ApprovalItem).OrderBy(i => i.Since).Take(Math.Clamp(take, 1, 200)).ToList();
    }

    /// <summary>
    /// The approvals asked of one person and still open: undecided, in the current stage of an approval step that is waiting, on a
    /// request in progress - with what a list shows of each (the step, the request, its flow, department and requester).
    /// </summary>
    private IQueryable<RequestApproval> OpenApprovalsFor(Guid? userId) => db.RequestApprovals.AsNoTracking()
        .Include(a => a.Step).ThenInclude(s => s.FlowStep)
        .Include(a => a.Step).ThenInclude(s => s.Request).ThenInclude(r => r.Flow)
        .Include(a => a.Step).ThenInclude(s => s.Request).ThenInclude(r => r.Department)
        .Include(a => a.Step).ThenInclude(s => s.Request).ThenInclude(r => r.Requester)
        .Where(a => a.ApproverId == userId && a.Decision == ApprovalDecision.Pending && a.Step.Status == RequestStepStatus.Ready
            && a.Step.Request.Status == RequestStatus.InProgress
            && a.StageOrder == a.Step.Approvals.Max(x => x.StageOrder));

    private static RequestActionItem ApprovalItem(RequestApproval a) =>
        new(a.Step.Request, a.Step, $"Approve \"{a.Step.FlowStep.Title}\"", a.Step.StartedAt ?? a.Step.Request.CreatedAt);

    /// <summary>The requests a manager sees (requests.manage): their department's, or every department's at All, filtered.</summary>
    public async Task<IReadOnlyList<Request>> DepartmentRequestsAsync(RequestFilter filter, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = Scoping.Requests(db.Requests.AsNoTracking().Include(r => r.Flow).Include(r => r.Department).Include(r => r.Requester), actor);
        if (filter.DepartmentId is Guid d) q = q.Where(r => r.DepartmentId == d);
        if (filter.Status is RequestStatus s) q = q.Where(r => r.Status == s);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var pattern = $"%{filter.Query.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.Number, pattern) || EF.Functions.ILike(r.Title, pattern) || EF.Functions.ILike(r.Requester.DisplayName, pattern));
        }
        return await q.OrderByDescending(r => r.Status == RequestStatus.InProgress).ThenByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(filter.Take, 1, 500)).ToListAsync(ct);
    }

    /// <summary>The request a task was created by, if any (§6.20): its id and number for the task page's link. The request page keeps its own door.</summary>
    public async Task<(Guid Id, string Number)?> ForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var hit = await db.RequestSteps.AsNoTracking().Where(s => s.TaskId == taskId).Select(s => new { s.Request.Id, s.Request.Number }).FirstOrDefaultAsync(ct);
        return hit is null ? null : (hit.Id, hit.Number);
    }

    /// <summary>
    /// The label of a picked asset, project or person, for a form shown again after a refused post; null when it isn't in the field's
    /// scope. <paramref name="stepId"/> is the request step being filled in, null while the request is being logged.
    /// </summary>
    public async Task<string?> PickLabelAsync(Guid fieldId, Guid id, Guid? stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var field = await db.RequestFormFields.AsNoTracking().Include(f => f.Step).ThenInclude(s => s.Flow).ThenInclude(fl => fl.Category)
            .FirstOrDefaultAsync(f => f.Id == fieldId, ct);
        if (field is null) return null;
        var requesterDepartment = await RequesterDepartmentAsync(actor, [field], stepId, ct);
        return (await ResolvePickAsync(field.Step.Flow, field, id, actor.UserId!.Value, requesterDepartment, ct))?.Label;
    }

    /// <summary>
    /// The asset types a form's Asset type fields offer (§6.20), by field: the active types within each field's scope, each with the
    /// group the list shows it under - its category for one department's own (the flow's, or the requester's), department and
    /// category for the whole company's. <paramref name="stepId"/> is the request step being filled in, null while logging.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>>> AssetTypeChoicesAsync(
        RequestFlow flow, RequestFlowStep form, Guid? stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var fields = form.Fields.Where(f => f.FieldType == RequestFieldType.AssetType).ToList();
        var result = new Dictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>>();
        if (fields.Count == 0) return result;
        var requesterDepartment = await RequesterDepartmentAsync(actor, fields, stepId, ct);
        var bounds = fields.ToDictionary(f => f.Id, f => RequestEngineRules.ScopeDepartment(f.PickerScope, flow.Category.DepartmentId, requesterDepartment));
        var query = db.AssetTypes.AsNoTracking().Where(t => !t.IsArchived);
        if (bounds.Values.All(b => b.Kept))
        {
            var departments = bounds.Values.Select(b => b.DepartmentId).OfType<Guid>().Distinct().ToList();
            query = query.Where(t => departments.Contains(t.DepartmentId));
        }
        var types = await query
            .OrderBy(t => t.Department.Name).ThenBy(t => t.Category).ThenBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Category, t.DepartmentId, Department = t.Department.Name })
            .ToListAsync(ct);
        foreach (var field in fields)
        {
            var bound = bounds[field.Id];
            result[field.Id] = types.Where(t => bound.Offers(t.DepartmentId))
                .Select(t => new RequestAssetTypeChoice(t.Id, t.Name, bound.Kept
                    ? t.Category ?? string.Empty
                    : string.Join(" · ", new[] { t.Department, t.Category }.Where(s => !string.IsNullOrEmpty(s)))))
                .ToList();
        }
        return result;
    }

    /// <summary>
    /// The assets a form's Asset fields list as buttons (§6.20), by field: every asset the field's scope offers the caller, by name, when
    /// there are at most <see cref="RequestFlowRules.AssetButtonsUpTo"/> - none at all is such a list. A field whose scope offers more
    /// isn't here: its picker searches instead. <paramref name="stepId"/> is the request step being filled in, null while logging.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequestLookupItem>>> AssetChoicesAsync(
        RequestFlow flow, RequestFlowStep form, Guid? stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var result = new Dictionary<Guid, IReadOnlyList<RequestLookupItem>>();
        var assetFields = form.Fields.Where(f => f.FieldType == RequestFieldType.Asset).ToList();
        var requesterDepartment = await RequesterDepartmentAsync(actor, assetFields, stepId, ct);
        foreach (var fields in assetFields.GroupBy(f => f.PickerScope))
        {
            var bound = RequestEngineRules.ScopeDepartment(fields.Key, flow.Category.DepartmentId, requesterDepartment);
            var offered = await AssetItemsAsync(OfferedAssets(fields.Key, bound, actor.UserId!.Value)
                .OrderBy(a => a.Name).ThenBy(a => a.AssetNumber).ThenBy(a => a.Id)
                .Take(RequestFlowRules.AssetButtonsUpTo + 1), ct);
            if (offered.Count > RequestFlowRules.AssetButtonsUpTo) continue;
            foreach (var field in fields) result[field.Id] = offered;
        }
        return result;
    }

    /// <summary>The departments whose requests the caller may list, for the Department tab's filter.</summary>
    public async Task<IReadOnlyList<Department>> ManagedDepartmentsAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        return actor.ScopeOf(Permission.RequestsManage) switch
        {
            PermissionScope.All => await db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct),
            PermissionScope.Department when actor.DepartmentId is Guid own => await db.Departments.AsNoTracking().Where(d => d.Id == own).ToListAsync(ct),
            _ => []
        };
    }

    /// <summary>
    /// The pickers' search for an Asset, Project or Person field (§6.20): up to <paramref name="take"/> matches within the field's scope,
    /// under the flow's authority - the configurer chose what the field offers - never disposed assets, archived projects or inactive people.
    /// <paramref name="stepId"/> is the request step being filled in, null while the request is being logged.
    /// </summary>
    public async Task<IReadOnlyList<RequestLookupItem>> LookupAsync(Guid fieldId, string? query, Guid? stepId, int take = 20, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var field = await db.RequestFormFields.AsNoTracking().Include(f => f.Step).ThenInclude(s => s.Flow).ThenInclude(fl => fl.Category)
            .FirstOrDefaultAsync(f => f.Id == fieldId, ct) ?? throw new NotFoundException("Field not found.");
        var t = query?.Trim();
        if (string.IsNullOrEmpty(t)) return [];
        var pattern = $"%{t}%";
        var me = actor.UserId!.Value;
        var n = Math.Clamp(take, 1, 50);
        var requesterDepartment = await RequesterDepartmentAsync(actor, [field], stepId, ct);
        var bound = RequestEngineRules.ScopeDepartment(field.PickerScope, field.Step.Flow.Category.DepartmentId, requesterDepartment);
        // A requester with no department: nothing to search, and the filters below would leave everything in.
        if (bound.OffersNothing) return [];
        switch (field.FieldType)
        {
            case RequestFieldType.Asset:
                return await AssetItemsAsync(OfferedAssets(field.PickerScope, bound, me)
                    .Where(a => EF.Functions.ILike(a.Name, pattern) || (a.AssetNumber != null && EF.Functions.ILike(a.AssetNumber, pattern))
                        || (a.SerialNumber != null && EF.Functions.ILike(a.SerialNumber, pattern)))
                    .OrderBy(a => a.Name).Take(n), ct);
            case RequestFieldType.Project:
                var projects = db.Projects.AsNoTracking().Where(p => p.Status == ProjectStatus.Active || p.Status == ProjectStatus.OnHold);
                if (bound.DepartmentId is Guid projectsOf) projects = projects.Where(p => p.DepartmentId == projectsOf);
                return await projects.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Number, pattern))
                    .OrderBy(p => p.Name).Take(n)
                    .Select(p => new RequestLookupItem(p.Id, p.Number + " " + p.Name, p.Department.Name, p.Status == ProjectStatus.OnHold ? "On hold" : null))
                    .ToListAsync(ct);
            case RequestFieldType.User:
                var people = db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsSystemAccount);
                if (bound.DepartmentId is Guid peopleOf) people = people.Where(u => u.DepartmentId == peopleOf);
                return await people.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || (u.Email != null && EF.Functions.ILike(u.Email, pattern)))
                    .OrderBy(u => u.DisplayName).Take(n)
                    .Select(u => new RequestLookupItem(u.Id, u.DisplayName, u.Department != null ? u.Department.Name : null, u.Email))
                    .ToListAsync(ct);
            default:
                return [];
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A picked asset, asset type, project or person, resolved: its label for the answer's value.</summary>
    private sealed record Pick(string Label, Guid? AssetId, Guid? ProjectId, Guid? UserId, Guid? AssetTypeId = null);

    private IQueryable<RequestFlow> OfferedFlows() => db.RequestFlows.AsNoTracking()
        .Where(RequestFlowRules.Offered)
        .Include(f => f.Category).ThenInclude(c => c.Department)
        .Include(f => f.Steps).ThenInclude(s => s.Fields)
        .Include(f => f.Steps).ThenInclude(s => s.Dependencies)
        .Include(f => f.Steps).ThenInclude(s => s.Stages).ThenInclude(st => st.Approvers)
        .Include(f => f.Steps).ThenInclude(s => s.Action).ThenInclude(a => a!.Parameters)
        .Include(f => f.Steps).ThenInclude(s => s.ActionInputs)
        .AsSplitQuery();

    /// <summary>
    /// The assets an Asset field's scope offers a person (§6.20): the ones they hold, the ones of the department the scope keeps to
    /// (the flow's, or the requester's - none for a requester with no department), or everyone's - never disposed ones.
    /// </summary>
    private IQueryable<Asset> OfferedAssets(RequestPickerScope? scope, PickerDepartment bound, Guid me)
    {
        var assets = db.Assets.AsNoTracking().Where(a => a.Status != AssetStatus.Disposed);
        return scope switch
        {
            RequestPickerScope.Held => assets.Where(a => a.Assignments.Any(x => x.UserId == me)),
            _ when bound.OffersNothing => assets.Where(a => false),
            _ when bound.DepartmentId is Guid own => assets.Where(a => a.DepartmentId == own),
            _ => assets
        };
    }

    /// <summary>
    /// The department of whoever logged the request a form belongs to, for the fields scoped to it (§6.20) - looked up only when one
    /// of <paramref name="fields"/> is. While the request is being logged there is no step yet and the caller is the requester; on a
    /// later form step it is the request's requester, whoever fills the form in, and the caller must be allowed to act on that step.
    /// </summary>
    private async Task<Guid?> RequesterDepartmentAsync(Actor actor, IEnumerable<RequestFormField> fields, Guid? stepId, CancellationToken ct)
    {
        var scoped = fields.FirstOrDefault(f => f.PickerScope == RequestPickerScope.RequestersDepartment);
        if (scoped is null) return null;
        if (stepId is null) return actor.DepartmentId;
        var step = await db.RequestSteps.AsNoTracking().Include(s => s.Request).ThenInclude(r => r.Requester)
            .FirstOrDefaultAsync(s => s.Id == stepId && s.FlowStepId == scoped.StepId, ct) ?? throw new NotFoundException("Request step not found.");
        AccessPolicy.Require(AccessPolicy.CanActOnStep(actor, step.Request, step), "This step isn't addressed to you.");
        return step.Request.Requester.DepartmentId;
    }

    /// <summary>Assets as a form shows them, searched or listed: the label, the type, and the location with the status when it isn't Active.</summary>
    private static async Task<IReadOnlyList<RequestLookupItem>> AssetItemsAsync(IQueryable<Asset> assets, CancellationToken ct)
    {
        var rows = await assets
            .Select(a => new { a.Id, a.AssetNumber, a.Name, Type = a.AssetType.Name, Location = a.AssetLocation != null ? a.AssetLocation.Name : null, a.Status })
            .ToListAsync(ct);
        return rows.Select(a => new RequestLookupItem(a.Id, AssetRules.Label(a.AssetNumber, a.Name), a.Type,
            string.Join(" · ", new[] { a.Location, a.Status == AssetStatus.Active ? null : a.Status.Label() }.OfType<string>()))).ToList();
    }

    private static void Order(RequestFlow flow)
    {
        flow.Steps = flow.Steps.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Title).ToList();
        foreach (var s in flow.Steps) s.Fields = s.Fields.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Prompt).ToList();
    }

    /// <summary>The form's answers cleaned and its picks resolved; refused ones reported per field. The department is the request's requester's.</summary>
    private async Task<(IReadOnlyList<RequestAnswer> Answers, Dictionary<Guid, Pick> Picks)> CleanFormAsync(
        RequestFlow flow, RequestFlowStep form, IReadOnlyDictionary<Guid, RequestAnswerInput> answers,
        IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentUpload>> files, Guid? requesterDepartment, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        var given = new Dictionary<Guid, RequestAnswerInput>(answers);
        foreach (var (fieldId, uploads) in files)
            given[fieldId] = new RequestAnswerInput(given.GetValueOrDefault(fieldId)?.Value, given.GetValueOrDefault(fieldId)?.Id, uploads.Count);
        var (clean, errors) = RequestEngineRules.CleanAnswers(form.Fields, given);
        var picks = new Dictionary<Guid, Pick>();
        var errs = new Dictionary<Guid, string>(errors);
        foreach (var answer in clean.Where(a => a.Id is not null))
        {
            var pick = await ResolvePickAsync(flow, answer.Field, answer.Id!.Value, actor.UserId!.Value, requesterDepartment, ct);
            if (pick is null) errs[answer.Field.Id] = answer.Field.FieldType switch
            {
                RequestFieldType.Asset => "Choose one of the assets offered.",
                RequestFieldType.AssetType => "Choose one of the asset types offered.",
                RequestFieldType.Project => "Choose one of the projects offered.",
                _ => "Choose one of the people offered."
            };
            else picks[answer.Field.Id] = pick;
        }
        if (errs.Count > 0) throw new RequestAnswersException(errs);
        return (clean, picks);
    }

    private async Task<Pick?> ResolvePickAsync(RequestFlow flow, RequestFormField field, Guid id, Guid me, Guid? requesterDepartment, CancellationToken ct)
    {
        var bound = RequestEngineRules.ScopeDepartment(field.PickerScope, flow.Category.DepartmentId, requesterDepartment);
        switch (field.FieldType)
        {
            case RequestFieldType.Asset:
                var asset = await db.Assets.AsNoTracking().Include(a => a.Assignments).FirstOrDefaultAsync(a => a.Id == id && a.Status != AssetStatus.Disposed, ct);
                if (asset is null) return null;
                var inScope = field.PickerScope == RequestPickerScope.Held ? asset.Assignments.Any(x => x.UserId == me) : bound.Offers(asset.DepartmentId);
                return inScope ? new Pick(AssetRules.Label(asset.AssetNumber, asset.Name), asset.Id, null, null) : null;
            case RequestFieldType.Project:
                var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Status != ProjectStatus.Archived, ct);
                if (project is null || !bound.Offers(project.DepartmentId)) return null;
                return new Pick($"{project.Number} {project.Name}", null, project.Id, null);
            case RequestFieldType.User:
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive && !u.IsSystemAccount, ct);
                if (user is null || !bound.Offers(user.DepartmentId)) return null;
                return new Pick(user.DisplayName, null, null, user.Id);
            case RequestFieldType.AssetType:
                var type = await db.AssetTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && !t.IsArchived, ct);
                if (type is null || !bound.Offers(type.DepartmentId)) return null;
                return new Pick(type.Name, null, null, null, type.Id);
            default:
                return null;
        }
    }

    /// <summary>Write a form's answers and files as rows on its step. Inside the caller's transaction; the fields are the form's, passed in since a new step has no navigation loaded.</summary>
    private async Task WriteAnswersAsync(
        Actor actor, Request request, RequestStep step, IEnumerable<RequestFormField> fields, IReadOnlyList<RequestAnswer> answers, Dictionary<Guid, Pick> picks,
        IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentUpload>> files, CancellationToken ct)
    {
        foreach (var a in answers.Where(a => a.Field.FieldType != RequestFieldType.Attachment))
        {
            var pick = picks.GetValueOrDefault(a.Field.Id);
            db.RequestFormAnswers.Add(new RequestFormAnswer
            {
                StepId = step.Id, FieldId = a.Field.Id, Value = pick?.Label ?? a.Value,
                AssetId = pick?.AssetId, AssetTypeId = pick?.AssetTypeId, ProjectId = pick?.ProjectId, UserId = pick?.UserId
            });
        }
        foreach (var field in fields.Where(f => f.FieldType == RequestFieldType.Attachment))
        {
            foreach (var upload in files.GetValueOrDefault(field.Id) ?? [])
            {
                var attachment = await attachments.AddToRequestAsync(actor, request, upload, ct);
                db.RequestFormAnswers.Add(new RequestFormAnswer { StepId = step.Id, FieldId = field.Id, Value = attachment.FileName, AttachmentId = attachment.Id });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<(Actor Actor, RequestStep Step)> ManagedStepAsync(Guid stepId, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        var step = await db.RequestSteps.AsNoTracking().Include(s => s.Request).FirstOrDefaultAsync(s => s.Id == stepId, ct)
            ?? throw new NotFoundException("Request step not found.");
        AccessPolicy.Require(AccessPolicy.CanRetryOrSkipStep(actor, step.Request), "Only a manager of the request's department can retry or skip a step.");
        return (actor, step);
    }

    private static void RequireSubmit(Actor actor) =>
        AccessPolicy.Require(AccessPolicy.CanSubmitRequests(actor), "You don't have permission to log requests.");
}

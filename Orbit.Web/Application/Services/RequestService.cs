using Microsoft.EntityFrameworkCore;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The Requests page (spec §6.20): every department's live request flows, logging a request as a task in the flow's department, and
/// the requests the caller has logged. Everything here needs requests.submit; what the flows are is <see cref="RequestCatalogueService"/>.
/// </summary>
public sealed class RequestService(
    ApplicationDbContext db,
    IActorProvider actors,
    TaskService tasks,
    AttachmentService attachments,
    AssetService assets)
{
    /// <summary>Every live category with its live options (<see cref="RequestRules.Live"/>), grouped by department in name order.</summary>
    public async Task<IReadOnlyList<RequestCatalogueSection>> CatalogueAsync(CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        var categories = await LiveCategoriesAsync(null, ct);
        return categories.GroupBy(c => c.DepartmentId)
            .Select(g => new RequestCatalogueSection(g.First().Department, g.ToList()))
            .OrderBy(s => s.Department.Name).ToList();
    }

    /// <summary>One live category with its live options; not found once archived or emptied.</summary>
    public async Task<RequestCategory> CategoryAsync(Guid id, CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        return (await LiveCategoriesAsync(id, ct)).FirstOrDefault()
            ?? throw new NotFoundException("That request category isn't available.");
    }

    /// <summary>A live flow with its category, department and questions - what the flow page walks through.</summary>
    public async Task<RequestOption> FlowAsync(Guid optionId, CancellationToken ct = default)
    {
        RequireSubmit(await actors.GetAsync(ct));
        var option = await db.RequestOptions.AsNoTracking()
            .Include(o => o.Category).ThenInclude(c => c.Department)
            .Include(o => o.Questions)
            .Where(o => o.Id == optionId && o.Kind == RequestOptionKind.Flow)
            .Where(RequestRules.Live)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("That request isn't available.");
        option.Questions = option.Questions.OrderBy(q => q.DisplayOrder).ToList();
        return option;
    }

    /// <summary>
    /// Log a request: check the answers (<see cref="RequestAnswersException"/> names each one refused), check the files, compose the
    /// task (<see cref="RequestRules.Compose"/>) and file it in the flow's department with the files attached, in one transaction - a
    /// file that fails leaves no task behind. A repeated post with the same idempotency key returns the request already logged.
    /// </summary>
    public async Task<TaskItem> SubmitAsync(
        Guid optionId, IReadOnlyDictionary<Guid, RequestAnswerInput> answers, IReadOnlyList<AttachmentUpload> files, string? idempotencyKey,
        CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is not null)
        {
            var done = await db.Tasks.AsNoTracking().Include(t => t.Department)
                .FirstOrDefaultAsync(t => t.IdempotencyKey == key && t.CreatedById == actor.UserId && t.Source == TaskSource.Request, ct);
            if (done is not null) return done;
        }

        var option = await FlowAsync(optionId, ct);
        // Who it is for: someone the requester may log for - and at Own always the requester, whatever was posted.
        var people = await PeopleAsync(actor, ct);
        var given = new Dictionary<Guid, RequestAnswerInput>(answers);
        if (!AccessPolicy.CanRequestForOthers(actor))
            foreach (var q in option.Questions.Where(q => q.QuestionType == RequestQuestionType.User))
                given[q.Id] = new RequestAnswerInput(actor.UserId.ToString());
        var (clean, errors) = RequestRules.CleanAnswers(option.Questions, given, DateOnly.FromDateTime(DateTime.UtcNow),
            people.ToDictionary(p => p.Id, p => p.Name));
        if (errors.Count > 0) throw new RequestAnswersException(errors);

        // A picked asset must be one the requester can see and that isn't disposed (§6.19) - said beside the question.
        string? assetLabel = null;
        if (clean.FirstOrDefault(a => a.AssetId is not null) is { } picked)
        {
            try
            {
                await assets.CheckLinkAsync(picked.AssetId, null, ct);
            }
            catch (ValidationException ex)
            {
                throw new RequestAnswersException(new Dictionary<Guid, string> { [picked.Question.Id] = ex.Message });
            }
            var asset = await db.Assets.AsNoTracking().Where(a => a.Id == picked.AssetId).Select(a => new { a.AssetNumber, a.Name }).FirstAsync(ct);
            assetLabel = AssetRules.Label(asset.AssetNumber, asset.Name);
        }
        if (files.Count > 0 && !option.AllowAttachments) throw new ValidationException("This request doesn't take files.");
        attachments.CheckUploads(files);

        var requester = await db.Users.AsNoTracking().Include(u => u.Department).FirstAsync(u => u.Id == actor.UserId, ct);
        var composed = RequestRules.Compose(option, clean, requester.DisplayName, requester.Department?.Name, assetLabel);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await tasks.CreateRequestAsync(new RequestTaskInput
        {
            DepartmentId = option.Category.DepartmentId,
            Title = composed.Title,
            Description = composed.Description,
            Priority = composed.Priority,
            Type = option.TaskType,
            DueDate = composed.DueDate,
            AssetId = composed.AssetId,
            RequestedForId = composed.RequestedForId,
            IdempotencyKey = key,
            RequestDetails = new { categoryId = option.CategoryId, category = option.Category.Title, optionId = option.Id, option = option.Title }
        }, ct);
        foreach (var file in files)
            await attachments.AddToLoggedRequestAsync(task, file, ct);
        await tx.CommitAsync(ct);
        return task;
    }

    /// <summary>
    /// Whom the caller may log a request for (a flow's User question), by name: active people, never the Claude user - everyone at
    /// All, their department's at Department, only themselves at Own.
    /// </summary>
    public async Task<IReadOnlyList<RequestPerson>> PeopleAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        return await PeopleAsync(actor, ct);
    }

    private async Task<IReadOnlyList<RequestPerson>> PeopleAsync(Actor actor, CancellationToken ct)
    {
        var me = actor.UserId;
        var dept = actor.DepartmentId;
        var q = db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsSystemAccount);
        q = actor.ScopeOf(Permission.RequestsSubmit) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(u => u.Id == me || (dept != null && u.DepartmentId == dept)),
            _ => q.Where(u => u.Id == me)
        };
        return await q.OrderBy(u => u.DisplayName).ThenBy(u => u.Email)
            .Select(u => new RequestPerson(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The requests the caller logged or that were logged for them (tasks with Source Request they created, or whose RequestedForId is
    /// theirs), open ones first and then the newest, each with whether they may open it: that follows tasks.view, which covers tasks
    /// they created from Own up.
    /// </summary>
    public async Task<IReadOnlyList<MyRequest>> MyRequestsAsync(int take = 20, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var me = actor.UserId;
        var rows = await db.Tasks.AsNoTracking().Include(t => t.Department).Include(t => t.CreatedBy).Include(t => t.RequestedFor)
            .Where(t => t.Source == TaskSource.Request && (t.CreatedById == me || t.RequestedForId == me))
            .OrderBy(t => t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled)
            .ThenByDescending(t => t.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return rows.Select(t => new MyRequest(t, AccessPolicy.CanViewTask(actor, t))).ToList();
    }

    /// <summary>One of the caller's own requests, for the page that confirms it was logged.</summary>
    public async Task<MyRequest> LoggedAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireSubmit(actor);
        var task = await db.Tasks.AsNoTracking().Include(t => t.Department)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.Source == TaskSource.Request && t.CreatedById == actor.UserId, ct)
            ?? throw new NotFoundException("Request not found.");
        return new MyRequest(task, AccessPolicy.CanViewTask(actor, task));
    }

    /// <summary>
    /// The live categories (all of them, or the one asked for), each holding only its live options in display order. Identity
    /// resolution makes the options of one category share one category object.
    /// </summary>
    private async Task<List<RequestCategory>> LiveCategoriesAsync(Guid? categoryId, CancellationToken ct)
    {
        var q = db.RequestOptions.AsNoTrackingWithIdentityResolution()
            .Include(o => o.Category).ThenInclude(c => c.Department)
            .Where(RequestRules.Live);
        if (categoryId is Guid id) q = q.Where(o => o.CategoryId == id);
        var options = await q.ToListAsync(ct);
        var categories = options.Select(o => o.Category).Distinct()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var c in categories)
            c.Options = options.Where(o => o.CategoryId == c.Id)
                .OrderBy(o => o.DisplayOrder).ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return categories;
    }

    private static void RequireSubmit(Actor actor) =>
        AccessPolicy.Require(AccessPolicy.CanSubmitRequests(actor), "You don't have permission to log requests.");
}

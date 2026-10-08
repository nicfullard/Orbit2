using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// All task reads and writes for both the Razor Pages UI and the MCP tools. View scoping and the permission
/// rules (§6.5) are enforced here, not in the UI.
/// </summary>
public sealed class TaskService(
    ApplicationDbContext db,
    NumberingService numbering,
    IActorProvider actors,
    AuditService audit,
    NotificationService notifications,
    TaskStructureService structure,
    AssetService assets,
    RequestEngine requests)
{
    private static IQueryable<TaskItem> WithIncludes(IQueryable<TaskItem> q) => q
        .Include(t => t.Department)
        .Include(t => t.ParentTask)
        .Include(t => t.Project).ThenInclude(p => p!.Department)
        .Include(t => t.CreatedBy)
        .Include(t => t.Requestee)
        .Include(t => t.Sprint)
        .Include(t => t.Asset)
        .Include(t => t.RecurringTaskDefinition);

    public async Task<PagedResult<TaskItem>> ListAsync(TaskFilter f, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = WithIncludes(db.Tasks.AsNoTracking());
        q = Scope(q, actor, f);

        if (f.ProjectId is Guid projectId) q = q.Where(t => t.ProjectId == projectId);
        if (f.Status is TaskItemStatus status) q = q.Where(t => t.Status == status);
        if (f.Unassigned) q = q.Where(t => !t.Assignments.Any());
        else if (f.AssigneeId is Guid assigneeId) q = q.Where(t => t.Assignments.Any(x => x.UserId == assigneeId));
        if (f.RequesteeId is Guid requesteeId) q = q.Where(t => t.RequesteeId == requesteeId);
        if (f.Priority is TaskPriority priority) q = q.Where(t => t.Priority == priority);
        if (f.Type is TaskType type) q = q.Where(t => t.Type == type);
        if (f.Source is TaskSource source) q = q.Where(t => t.Source == source);
        if (f.DueBefore is DateOnly before) q = q.Where(t => t.DueDate != null && t.DueDate <= before);
        if (f.DueAfter is DateOnly after) q = q.Where(t => t.DueDate != null && t.DueDate >= after);
        if (f.SprintId is Guid sprintId) q = q.Where(t => t.SprintId == sprintId);
        if (f.RecurringTaskDefinitionId is Guid defId) q = q.Where(t => t.RecurringTaskDefinitionId == defId);
        if (f.ParentTaskId is Guid parentId) q = q.Where(t => t.ParentTaskId == parentId);
        if (f.AssetId is Guid assetId) q = q.Where(t => t.AssetId == assetId);
        if (f.BacklogOnly) q = q.Where(t => t.SprintId == null);
        if (f.OpenOnly) q = q.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        var plannedFor = f.PlannedFor ?? (f.PlannedToday ? DateOnly.FromDateTime(DateTime.UtcNow) : null);
        if (plannedFor is DateOnly planned) q = q.Where(t => t.PlannedFor == planned);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = $"%{f.Search.Trim()}%";
            q = q.Where(t => EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Description ?? "", pattern) || EF.Functions.ILike(t.Number, pattern));
        }

        var page = Math.Max(1, f.Page);
        var pageSize = Math.Clamp(f.PageSize, 1, 500);
        var total = await q.CountAsync(ct);
        var items = await q
            .OrderBy(t => t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled)
            .ThenBy(t => t.DueDate == null)
            .ThenBy(t => t.DueDate)
            .ThenByDescending(EnumOrder.ByTaskPriority)
            .ThenByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<TaskItem>(items, page, pageSize, total);
    }

    /// <summary>
    /// The actor's tasks.view scope (§6.5), then the optional department filter within it. A requestee filter (§6.2.2) lists every
    /// task the actor may open, their own ones in other departments included.
    /// </summary>
    private static IQueryable<TaskItem> Scope(IQueryable<TaskItem> q, Actor actor, TaskFilter f)
    {
        q = f.RequesteeId is null ? Scoping.Tasks(q, actor) : Scoping.TasksIncludingOwn(q, actor);
        return f.DepartmentId is Guid d ? q.Where(t => t.DepartmentId == d) : q;
    }

    public async Task<TaskItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        // The asset's holders decide whether the viewer may open the asset (§6.19), so the task page links it only then.
        // Who assigned each assignee, and their department, are for the task page's Assignees card (§6.2.3).
        var task = await WithIncludes(db.Tasks).Include(t => t.Asset!.Assignments)
            .Include(t => t.Assignments).ThenInclude(x => x.AssignedBy)
            .Include(t => t.Assignments).ThenInclude(x => x.User).ThenInclude(u => u.Department)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        return task;
    }

    public async Task<IReadOnlyList<TaskItem>> GetMyTasksAsync(bool openOnly = true, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) return [];
        var q = WithIncludes(db.Tasks.AsNoTracking()).Where(t => t.Assignments.Any(x => x.UserId == me));
        if (openOnly) q = q.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        return await q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ThenByDescending(EnumOrder.ByTaskPriority).ThenByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public async Task<TaskItem> CreateAsync(TaskInput input, TaskSource source, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var title = RequireTitle(input.Title);

        if (!string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            var key = input.IdempotencyKey.Trim();
            var existing = await WithIncludes(db.Tasks).FirstOrDefaultAsync(t => t.IdempotencyKey == key, ct);
            if (existing is not null) return existing;
        }

        var departmentId = await ResolveDepartmentAsync(input.ProjectId, input.DepartmentId, actor, existing: null, ct);
        AccessPolicy.Require(AccessPolicy.CanCreateTaskIn(actor, departmentId), "You don't have permission to create tasks in this department.");
        await RequireOpenDepartmentAsync(departmentId, ct);

        var assignees = await AssigneeResolver.ResolveAsync(db, [], input.AssigneeIds ?? [], departmentId, departmentChanging: false, ct);
        var requestee = await ValidateRequesteeAsync(actor, input.RequesteeId, ct);
        await ValidateSprintAsync(input.SprintId, ct);
        await assets.CheckLinkAsync(input.AssetId, null, ct);

        var status = input.Status ?? TaskItemStatus.Todo;
        DependencyRules.RequireDatesInOrder(input.StartDate, input.DueDate);
        var parent = await structure.ValidateParentAsync(null, input.ParentTaskId, input.ProjectId, departmentId, childOpen: !status.IsClosed(), ct);

        var task = new TaskItem
        {
            Title = title,
            Description = Clean(input.Description),
            DepartmentId = departmentId,
            ProjectId = input.ProjectId,
            AssetId = input.AssetId,
            Priority = input.Priority,
            Type = input.Type,
            EstimateMinutes = CleanEstimate(input.EstimateMinutes),
            Assignments = [],
            RequesteeId = requestee?.Id,
            ParentTaskId = parent?.Id,
            StartDate = input.StartDate,
            DueDate = input.DueDate,
            SprintId = input.SprintId,
            Source = source,
            IdempotencyKey = Clean(input.IdempotencyKey)
        };
        foreach (var assignee in assignees.Added)
            task.Assignments.Add(new TaskAssignment { TaskId = task.Id, UserId = assignee.Id, AssignedById = actor.UserId });
        await InsertAsync(actor, task, status, null, ct);

        var told = await NotifyAssignedAsync(task, assignees.Added, actor, ct);
        // The requestee hears of it unless they made it, or the assignment email has just told them (§6.2.2).
        if (requestee is not null && requestee.Id != actor.UserId && !told.Contains(requestee.Id))
            await notifications.TaskCreatedForAsync(task, requestee, actor, ct: ct);

        return await GetAsync(task.Id, ct);
    }

    /// <summary>Number, stamp and save a new task with its Created audit entry - the part every way of creating a task shares.</summary>
    private async Task InsertAsync(Actor actor, TaskItem task, TaskItemStatus status, object? request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        task.Number = await numbering.NextAsync(NumberingService.TaskPrefix, now, ct);
        task.CreatedById = actor.UserId;
        task.CreatedAt = now;
        task.UpdatedAt = now;
        ApplyStatus(task, status, now);

        db.Tasks.Add(task);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Created, task.DepartmentId, task.Title, new
        {
            task.Number, task.Title, task.Status, task.Priority, task.Type, task.EstimateMinutes, task.ProjectId, task.DepartmentId, task.AssigneeIds,
            task.ParentTaskId, task.StartDate, task.DueDate, task.Source, task.SprintId, task.AssetId, task.RequesteeId, request
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Full edit: the input is the complete new state (a null due date or sprint clears it; null assignees keeps them, an empty list clears them).</summary>
    public async Task<TaskItem> UpdateAsync(Guid id, TaskInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await WithIncludes(db.Tasks).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");

        var title = RequireTitle(input.Title);
        var departmentId = await ResolveDepartmentAsync(input.ProjectId, input.DepartmentId, actor, task, ct);
        if (departmentId != task.DepartmentId)
        {
            AccessPolicy.Require(AccessPolicy.CanMoveTaskTo(actor, departmentId), "You don't have permission to move tasks into that department.");
            await RequireOpenDepartmentAsync(departmentId, ct);
        }

        // §6.2.3: the people added are checked; the people kept only when the task is moving to another department.
        var assignees = await AssigneeResolver.ResolveAsync(db, task.AssigneeIds, input.AssigneeIds, departmentId,
            departmentChanging: departmentId != task.DepartmentId, ct);
        // §6.2.2: a kept requestee isn't re-checked; changing one needs tasks.create_for over the person removed and the person added.
        ApplicationUser? newRequestee = null;
        var requesteeChanged = input.RequesteeId != task.RequesteeId;
        if (requesteeChanged)
        {
            if (task.Requestee is { } removed)
                AccessPolicy.Require(AccessPolicy.CanCreateTaskFor(actor, removed.Id, removed.DepartmentId), "You can't change this task's requestee.");
            newRequestee = await ValidateRequesteeAsync(actor, input.RequesteeId, ct);
        }
        await assets.CheckLinkAsync(input.AssetId, task.AssetId, ct); // a kept asset isn't re-checked (§6.19)
        var newStatus = input.Status ?? task.Status; // any status: the §6.5 status rule is the edit right required above
        if (input.SprintId != task.SprintId)
        {
            AccessPolicy.Require(AccessPolicy.CanPlanTask(actor, task), "You can't plan tasks from another department.");
            await ValidateSprintAsync(input.SprintId, ct);
        }
        DependencyRules.RequireDatesInOrder(input.StartDate, input.DueDate);
        if (newStatus != task.Status) await structure.EnsureStatusChangeAllowedAsync(task, newStatus, ct);
        // §6.15: a child can't leave its parent's project on its own, a parent takes its subtree along, and no link may end up crossing projects.
        var subtree = await structure.PrepareMoveAsync(task, input.ProjectId, departmentId, input.ParentTaskId, actor, ct);
        var parent = await structure.ValidateParentAsync(task, input.ParentTaskId, input.ProjectId, departmentId, childOpen: !newStatus.IsClosed(), ct);

        var previousParentId = task.ParentTaskId;
        var estimate = CleanEstimate(input.EstimateMinutes);
        var changes = new ChangeSet()
            .TrackText("title", task.Title, title)
            .TrackText("description", task.Description, input.Description)
            .Track("departmentId", task.DepartmentId, departmentId)
            .Track("projectId", task.ProjectId, input.ProjectId)
            .Track("assetId", task.AssetId, input.AssetId)
            .Track("priority", task.Priority, input.Priority)
            .Track("type", task.Type, input.Type)
            .Track("estimateMinutes", task.EstimateMinutes, estimate)
            .Track("requesteeId", task.RequesteeId, input.RequesteeId)
            .Track("parentTaskId", task.ParentTaskId, parent?.Id)
            .Track("startDate", task.StartDate, input.StartDate)
            .Track("dueDate", task.DueDate, input.DueDate)
            .Track("status", task.Status, newStatus)
            .Track("sprintId", task.SprintId, input.SprintId);

        if (!changes.HasChanges && assignees.IsEmpty) return task;

        var now = DateTime.UtcNow;
        task.Title = title;
        task.Description = Clean(input.Description);
        task.DepartmentId = departmentId;
        task.ProjectId = input.ProjectId;
        task.AssetId = input.AssetId;
        task.Priority = input.Priority;
        task.Type = input.Type;
        task.EstimateMinutes = estimate;
        ApplyAssignees(actor, task, assignees, now);
        if (requesteeChanged)
        {
            task.RequesteeId = newRequestee?.Id;
            task.Requestee = newRequestee;
        }
        if (changes.Contains("dueDate")) task.DueSoonNotifiedAt = null; // a new due date earns a fresh reminder
        task.ParentTaskId = parent?.Id;
        task.StartDate = input.StartDate;
        task.DueDate = input.DueDate;
        task.SprintId = input.SprintId;
        if (newStatus != task.Status) ApplyStatus(task, newStatus, now);
        task.UpdatedAt = now;

        // A parent takes its subtree along when it changes project (or, standalone, department) - §6.15.
        foreach (var child in subtree)
        {
            var childChanges = new ChangeSet()
                .Track("projectId", child.ProjectId, input.ProjectId)
                .Track("departmentId", child.DepartmentId, input.ProjectId is null ? departmentId : child.DepartmentId);
            child.ProjectId = input.ProjectId;
            if (input.ProjectId is null) child.DepartmentId = departmentId;
            child.UpdatedAt = now;
            var details = new Dictionary<string, object?>(childChanges.Changes) { ["movedWithParent"] = task.Title };
            audit.Add(actor, AuditEntity.Task, child.Id, AuditAction.Updated, child.DepartmentId, child.Title, details);
        }

        var action = changes.Contains("status")
            ? (newStatus == TaskItemStatus.Done ? AuditAction.Completed : AuditAction.StatusChanged)
            : AuditAction.Updated;
        if (changes.Changes.Keys.Any(k => k != "parentTaskId"))
            audit.Add(actor, AuditEntity.Task, task.Id, action, departmentId, task.Title, changes.Changes);
        if (changes.Contains("parentTaskId"))
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.ParentChanged, departmentId, task.Title,
                new { parent = new { from = previousParentId, to = parent?.Id }, parentTitle = parent?.Title });
        await db.SaveChangesAsync(ct);
        // A task a request step created completes (or cancels) the step when it closes (§6.20).
        if (changes.Contains("status") && newStatus.IsClosed()) await requests.OnTaskClosedAsync(task, actor, ct);

        var told = await NotifyAssignedAsync(task, assignees.Added, actor, ct);
        if (newRequestee is not null && newRequestee.Id != actor.UserId && !told.Contains(newRequestee.Id))
            await notifications.TaskCreatedForAsync(task, newRequestee, actor, namedLater: true, ct);

        return await GetAsync(task.Id, ct);
    }

    /// <summary>Quick inline status change (the kanban / list control).</summary>
    public async Task<TaskItem> ChangeStatusAsync(Guid id, TaskItemStatus status, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanChangeStatus(actor, task), "You don't have permission to change this task's status.");
        if (task.Status == status) return task;
        await structure.EnsureStatusChangeAllowedAsync(task, status, ct);

        var previous = task.Status;
        var now = DateTime.UtcNow;
        ApplyStatus(task, status, now);
        task.UpdatedAt = now;
        audit.Add(actor, AuditEntity.Task, task.Id,
            status == TaskItemStatus.Done ? AuditAction.Completed : AuditAction.StatusChanged,
            task.DepartmentId, task.Title, new { status = new { from = previous, to = status } });
        await db.SaveChangesAsync(ct);
        if (status.IsClosed()) await requests.OnTaskClosedAsync(task, actor, ct);
        return task;
    }

    /// <summary>
    /// Quick inline assignee change (the task list control, §6.2.3): make this person the task's one assignee, or leave it with
    /// nobody. Same rights as a full edit. The control is offered only while a task has at most one assignee, so a task that has
    /// since gained several is left as it is rather than have them dropped by a stale page.
    /// </summary>
    public async Task<TaskItem> SetSoleAssigneeAsync(Guid id, Guid? assigneeId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        // Taking an unassigned task for yourself (§6.5) needs no edit rights, only tasks.take in its department.
        if (assigneeId is not null && assigneeId == actor.UserId && !AccessPolicy.CanEditTask(actor, task) && AccessPolicy.CanTakeTask(actor, task))
            return await TakeAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");
        if (task.Assignments.Count > 1)
            throw new ValidationException($"\"{task.Title}\" now has several assignees ({task.AssigneeNames}). Open the task to change them.");
        return await ChangeAssigneesAsync(actor, task, assigneeId is Guid one ? [one] : [], ct);
    }

    /// <summary>Add one person to a task's assignees (the task page, §6.2.3). Same rights as a full edit; someone already on it is left as is.</summary>
    public async Task<TaskItem> AddAssigneeAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var (actor, task) = await LoadForAssigneeChangeAsync(id, ct);
        return await ChangeAssigneesAsync(actor, task, [.. task.AssigneeIds, userId], ct);
    }

    /// <summary>Take one person off a task's assignees (the task page, §6.2.3). Same rights as a full edit.</summary>
    public async Task<TaskItem> RemoveAssigneeAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var (actor, task) = await LoadForAssigneeChangeAsync(id, ct);
        return await ChangeAssigneesAsync(actor, task, task.AssigneeIds.Where(x => x != userId).ToList(), ct);
    }

    private async Task<(Actor Actor, TaskItem Task)> LoadForAssigneeChangeAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");
        return (actor, task);
    }

    /// <summary>Set a task's assignees to <paramref name="wanted"/>, save, and tell the people added. The caller has checked the actor's rights.</summary>
    private async Task<TaskItem> ChangeAssigneesAsync(Actor actor, TaskItem task, IReadOnlyCollection<Guid> wanted, CancellationToken ct)
    {
        var change = await AssigneeResolver.ResolveAsync(db, task.AssigneeIds, wanted, task.DepartmentId, departmentChanging: false, ct);
        if (change.IsEmpty) return task;

        var now = DateTime.UtcNow;
        ApplyAssignees(actor, task, change, now);
        task.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await NotifyAssignedAsync(task, change.Added, actor, ct);
        return task;
    }

    /// <summary>
    /// Write a change of assignees (§6.2.3) with its audit entries - one per person, as an asset's holders are recorded (§6.19).
    /// The caller saves, and stamps the task's UpdatedAt in the same save: every change of assignees writes the task row, which
    /// is what <see cref="TakeAsync"/> queues behind.
    /// </summary>
    private void ApplyAssignees(Actor actor, TaskItem task, AssigneeResolver.Change change, DateTime now)
    {
        foreach (var row in task.Assignments.Where(x => change.Removed.Contains(x.UserId)).ToList())
        {
            db.TaskAssignments.Remove(row);
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.AssigneeRemoved, task.DepartmentId, task.Title,
                new { userId = row.UserId, user = row.User.DisplayName });
        }
        foreach (var assignee in change.Added)
        {
            db.TaskAssignments.Add(new TaskAssignment { TaskId = task.Id, UserId = assignee.Id, AssignedAt = now, AssignedById = actor.UserId });
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.AssigneeAdded, task.DepartmentId, task.Title,
                new { userId = assignee.Id, user = assignee.DisplayName });
        }
    }

    /// <summary>Email each person just assigned, other than whoever assigned them (§6.7). Returns who was told, for the requestee's email to skip.</summary>
    private async Task<HashSet<Guid>> NotifyAssignedAsync(TaskItem task, IReadOnlyList<ApplicationUser> added, Actor actor, CancellationToken ct)
    {
        var told = AssigneeRules.ToNotify(added.Select(u => u.Id), actor.UserId).ToHashSet();
        foreach (var assignee in added.Where(u => told.Contains(u.Id)))
            await notifications.TaskAssignedAsync(task, assignee, actor, ct);
        return told;
    }

    /// <summary>
    /// Take an unassigned task (§6.5): assign an open, unassigned task within the actor's tasks.take reach to the actor - the
    /// one assignee change someone may make on a task they can't otherwise edit. First come, first served: takers queue on the
    /// task's row, and each looks for an assignee only once it holds the row, so two people taking it at the same moment can't
    /// both win.
    /// </summary>
    public async Task<TaskItem> TakeAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) throw new ForbiddenException("Only a signed-in user can take a task.");
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        if (task.IsAssignedTo(me)) return await GetAsync(id, ct);
        if (!task.IsUnassigned)
            throw new ValidationException($"\"{task.Title}\" is already assigned to {task.AssigneeNames}.");
        if (!task.IsOpen) throw new ValidationException("A closed task can't be taken.");
        AccessPolicy.Require(AccessPolicy.CanTakeTask(actor, task), "You don't have permission to take tasks in this department.");
        var taker = (await AssigneeResolver.ResolveAsync(db, [], [me], task.DepartmentId, departmentChanging: false, ct)).Added[0];

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Writing the task row locks it: a second taker waits here until this one commits or gives up.
        var open = await db.Tasks.Where(t => t.Id == id && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, now), ct);
        if (open == 0) throw new ValidationException("A closed task can't be taken.");
        // A statement of its own, run with the row held, so it sees what an earlier taker committed. As part of the update's
        // WHERE it would not: after waiting for a lock PostgreSQL re-checks only the row it waited for.
        var holders = await db.TaskAssignments.AsNoTracking().Where(x => x.TaskId == id)
            .OrderBy(x => x.User.DisplayName).Select(x => new { x.UserId, x.User.DisplayName }).ToListAsync(ct);
        if (holders.Any(h => h.UserId == me)) return await GetAsync(id, ct);
        if (holders.Count > 0)
            throw new ValidationException($"\"{task.Title}\" was just taken by {string.Join(", ", holders.Select(h => h.DisplayName))}.");

        db.TaskAssignments.Add(new TaskAssignment { TaskId = id, UserId = me, AssignedAt = now, AssignedById = me });
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.AssigneeAdded, task.DepartmentId, task.Title,
            new { userId = me, user = taker.DisplayName, taken = true });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Quick inline due-date change (the task list control). Same rights as a full edit.</summary>
    public async Task<TaskItem> ChangeDueDateAsync(Guid id, DateOnly? dueDate, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");
        if (task.DueDate == dueDate) return task;
        DependencyRules.RequireDatesInOrder(task.StartDate, dueDate);

        var changes = new ChangeSet().Track("dueDate", task.DueDate, dueDate);
        task.DueDate = dueDate;
        task.DueSoonNotifiedAt = null; // a new due date earns a fresh reminder
        task.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Updated, task.DepartmentId, task.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>
    /// The Gantt's drag-to-reschedule (§6.16): set both planned dates at once. Same rights as a full edit, the same
    /// "start not after due" rule, and an ordinary <c>Updated</c> audit entry. Only this task moves - successors never shift.
    /// </summary>
    public async Task<TaskItem> ChangeDatesAsync(Guid id, DateOnly? startDate, DateOnly? dueDate, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");
        DependencyRules.RequireDatesInOrder(startDate, dueDate);

        var changes = new ChangeSet()
            .Track("startDate", task.StartDate, startDate)
            .Track("dueDate", task.DueDate, dueDate);
        if (!changes.HasChanges) return task;
        task.StartDate = startDate;
        if (changes.Contains("dueDate"))
        {
            task.DueDate = dueDate;
            task.DueSoonNotifiedAt = null; // a new due date earns a fresh reminder
        }
        task.UpdatedAt = DateTime.UtcNow;
        var details = new Dictionary<string, object?>(changes.Changes) { ["rescheduledOn"] = "gantt" };
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Updated, task.DepartmentId, task.Title, details);
        await db.SaveChangesAsync(ct);
        return task;
    }

    // --- Day plan (§6.12) ---------------------------------------------------------------------------

    /// <summary>Put a task on (or take it off) a day's plan. Anyone who may plan the task; closed tasks can't be planned.</summary>
    public async Task<TaskItem> SetPlannedForAsync(Guid id, DateOnly? date, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanPlanTask(actor, task), "You can't plan tasks from another department.");
        if (date is not null && !task.IsOpen) throw new ValidationException("Closed tasks can't be put on a day plan.");
        if (task.PlannedFor == date) return task;

        var changes = new ChangeSet().Track("plannedFor", task.PlannedFor, date);
        task.PlannedFor = date;
        task.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Task, task.Id, date is null ? AuditAction.Unplanned : AuditAction.Planned,
            task.DepartmentId, task.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>Carry tasks over to another day's plan. Closed tasks and ones already on that day are skipped. Returns the number moved.</summary>
    public async Task<int> CarryOverAsync(IReadOnlyCollection<Guid> taskIds, DateOnly to, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(ct);
        var moved = 0;
        var now = DateTime.UtcNow;
        foreach (var task in tasks)
        {
            AccessPolicy.Require(AccessPolicy.CanPlanTask(actor, task), $"You can't plan \"{task.Title}\" - it belongs to another department.");
            if (!task.IsOpen || task.PlannedFor == to) continue;
            var previous = task.PlannedFor;
            task.PlannedFor = to;
            task.UpdatedAt = now;
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Planned, task.DepartmentId, task.Title,
                new { plannedFor = new { from = previous, to }, carriedOver = true });
            moved++;
        }
        if (moved > 0) await db.SaveChangesAsync(ct);
        return moved;
    }

    /// <summary>The day plan for one date, scoped like <see cref="ListAsync"/> (someone who sees every department may narrow to one).</summary>
    public async Task<DayPlan> GetDayPlanAsync(DateOnly date, Guid? departmentId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var scoped = Scope(WithIncludes(db.Tasks.AsNoTracking()), actor, new TaskFilter { DepartmentId = departmentId });

        var planned = await scoped.Where(t => t.PlannedFor == date)
            .OrderBy(t => t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled)
            .ThenBy(t => !t.Assignments.Any()).ThenBy(t => t.Assignments.Min(x => x.User.DisplayName))
            .ThenByDescending(EnumOrder.ByTaskPriority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ToListAsync(ct);

        // Whatever is still open on the most recent earlier plan - "what didn't get finished last time".
        var leftOver = scoped.Where(t => t.PlannedFor != null && t.PlannedFor < date
            && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        var previous = await leftOver.MaxAsync(t => t.PlannedFor, ct);
        List<TaskItem> unfinished = previous is null ? [] : await leftOver.Where(t => t.PlannedFor == previous)
            .OrderBy(t => !t.Assignments.Any()).ThenBy(t => t.Assignments.Min(x => x.User.DisplayName)).ThenByDescending(EnumOrder.ByTaskPriority)
            .ToListAsync(ct);

        // How often each of them has slipped to a later day, from their Planned audit rows - one query for the page.
        var ids = planned.Concat(unfinished).Select(t => t.Id).ToList();
        var entries = ids.Count == 0 ? [] : await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntity.Task && a.Action == AuditAction.Planned && ids.Contains(a.EntityId))
            .Select(a => new { a.EntityId, a.Details })
            .ToListAsync(ct);
        var carryOvers = DayPlanRules.CountCarryOvers(entries.Select(a => (a.EntityId, (string?)a.Details)));
        return new DayPlan { Date = date, Planned = planned, PreviousDate = previous, Unfinished = unfinished, CarryOvers = carryOvers };
    }

    /// <summary>Plan tasks into a sprint (or back to the backlog with a null sprint). Returns the number moved.</summary>
    public async Task<int> MoveToSprintAsync(IReadOnlyCollection<Guid> taskIds, Guid? sprintId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var sprint = await ValidateSprintAsync(sprintId, ct);
        var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(ct);
        var moved = 0;
        var now = DateTime.UtcNow;
        foreach (var task in tasks)
        {
            AccessPolicy.Require(AccessPolicy.CanPlanTask(actor, task),
                $"You can't plan tasks from another department (\"{task.Title}\").");
            if (task.SprintId == sprintId) continue;
            if (sprintId is not null && task.Status.IsClosed()) continue; // closed work doesn't get planned

            var previous = task.SprintId;
            task.SprintId = sprintId;
            task.UpdatedAt = now;
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.SprintChanged, task.DepartmentId, task.Title,
                new { sprintId = new { from = previous, to = sprintId }, sprintName = sprint?.Name });
            moved++;
        }
        await db.SaveChangesAsync(ct);
        return moved;
    }

    // --- helpers -------------------------------------------------------------------------

    private static string RequireTitle(string? title)
    {
        var t = title?.Trim();
        if (string.IsNullOrEmpty(t)) throw new ValidationException("Title is required.");
        if (t.Length > 300) throw new ValidationException("Title must be 300 characters or fewer.");
        return t;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>An estimate (§6.10) is whole minutes: null or 0 clears it; at most a year.</summary>
    private static int? CleanEstimate(int? minutes)
    {
        if (minutes is null or 0) return null;
        if (minutes < 0) throw new ValidationException("The estimate can't be negative.");
        if (minutes > 525_600) throw new ValidationException("The estimate must be at most a year (525,600 minutes).");
        return minutes;
    }

    private static void ApplyStatus(TaskItem task, TaskItemStatus status, DateTime now)
    {
        if (task.Status == TaskItemStatus.Todo && status != TaskItemStatus.Todo && task.FirstRespondedAt is null)
            task.FirstRespondedAt = now;
        task.CompletedAt = status == TaskItemStatus.Done ? now : null;
        task.Status = status;
    }

    /// <summary>
    /// Which department a task belongs to (§5.1, §6.2.1):
    /// <list type="bullet">
    /// <item>Standalone: the requested department, else the task's current one, else the caller's own.</item>
    /// <item>On a project: defaults to the project's department. A task already on that project keeps its own
    /// department when none is requested, so an edit never moves it silently.</item>
    /// <item>A department other than the project's makes it a cross-department project task. Introducing that pairing
    /// needs tasks.create everywhere; once filed, that department works the task like any other of theirs.</item>
    /// </list>
    /// Filing a task under a project needs tasks.create in the project's department.
    /// </summary>
    private async Task<Guid> ResolveDepartmentAsync(
        Guid? projectId, Guid? requestedDepartmentId, Actor actor, TaskItem? existing, CancellationToken ct)
    {
        if (projectId is not Guid pid)
        {
            return requestedDepartmentId ?? existing?.DepartmentId ?? actor.DepartmentId
                ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct)
            ?? throw new NotFoundException("Project not found.");
        var sameProject = existing?.ProjectId == pid;
        if (!sameProject)
        {
            if (project.Status == ProjectStatus.Archived)
                throw new ValidationException("Tasks can't be added to an archived project.");
            AccessPolicy.Require(AccessPolicy.CanAddTaskToProject(actor, project),
                "You don't have permission to add tasks to this project.");
        }

        var departmentId = requestedDepartmentId ?? (sameProject ? existing!.DepartmentId : project.DepartmentId);
        if (departmentId == project.DepartmentId) return departmentId;

        var alreadyFiledThere = sameProject && existing!.DepartmentId == departmentId;
        if (!alreadyFiledThere)
            AccessPolicy.Require(AccessPolicy.CanFileCrossDepartmentTask(actor),
                "Filing a task for another department under this project needs the Create tasks permission for all departments.");
        return departmentId;
    }

    private async Task RequireOpenDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
    }

    /// <summary>
    /// The requestee (§6.2.2) must be an active person - never the Claude user - whom the caller may create tasks for: themselves
    /// at Own, their department at Department, anyone at All (tasks.create_for).
    /// </summary>
    private async Task<ApplicationUser?> ValidateRequesteeAsync(Actor actor, Guid? requesteeId, CancellationToken ct)
    {
        if (requesteeId is not Guid id) return null;
        var person = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new ValidationException("The requestee doesn't exist.");
        if (!person.IsActive || person.IsSystemAccount)
            throw new ValidationException("A task can only be created for an active person.");
        AccessPolicy.Require(AccessPolicy.CanCreateTaskFor(actor, person.Id, person.DepartmentId),
            "You don't have permission to create tasks for that person.");
        return person;
    }

    private async Task<Sprint?> ValidateSprintAsync(Guid? sprintId, CancellationToken ct)
    {
        if (sprintId is not Guid id) return null;
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Sprint not found.");
        if (sprint.Status == SprintStatus.Completed)
            throw new ValidationException("Tasks can't be planned into a completed sprint.");
        return sprint;
    }
}

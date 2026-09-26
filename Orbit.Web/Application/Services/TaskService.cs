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
    AssetService assets)
{
    private static IQueryable<TaskItem> WithIncludes(IQueryable<TaskItem> q) => q
        .Include(t => t.Department)
        .Include(t => t.ParentTask)
        .Include(t => t.Project).ThenInclude(p => p!.Department)
        .Include(t => t.Assignee)
        .Include(t => t.CreatedBy)
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
        if (f.Unassigned) q = q.Where(t => t.AssigneeId == null);
        else if (f.AssigneeId is Guid assigneeId) q = q.Where(t => t.AssigneeId == assigneeId);
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
            .ThenByDescending(t => t.Priority)
            .ThenByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<TaskItem>(items, page, pageSize, total);
    }

    /// <summary>The actor's tasks.view scope (§6.5), then the optional department filter within it.</summary>
    private static IQueryable<TaskItem> Scope(IQueryable<TaskItem> q, Actor actor, TaskFilter f)
    {
        q = Scoping.Tasks(q, actor);
        return f.DepartmentId is Guid d ? q.Where(t => t.DepartmentId == d) : q;
    }

    public async Task<TaskItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        // The asset's holders decide whether the viewer may open the asset (§6.19), so the task page links it only then.
        var task = await WithIncludes(db.Tasks).Include(t => t.Asset!.Assignments).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        return task;
    }

    public async Task<IReadOnlyList<TaskItem>> GetMyTasksAsync(bool openOnly = true, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is null) return [];
        var q = WithIncludes(db.Tasks.AsNoTracking()).Where(t => t.AssigneeId == actor.UserId);
        if (openOnly) q = q.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        return await q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ThenByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt).ToListAsync(ct);
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

        var assignee = await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
        await ValidateSprintAsync(input.SprintId, ct);
        await assets.CheckLinkAsync(input.AssetId, null, ct);

        var status = input.Status ?? TaskItemStatus.Todo;
        DependencyRules.RequireDatesInOrder(input.StartDate, input.DueDate);
        var parent = await structure.ValidateParentAsync(null, input.ParentTaskId, input.ProjectId, departmentId, childOpen: !status.IsClosed(), ct);

        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Number = await numbering.NextAsync(NumberingService.TaskPrefix, now, ct),
            Title = title,
            Description = Clean(input.Description),
            DepartmentId = departmentId,
            ProjectId = input.ProjectId,
            AssetId = input.AssetId,
            Priority = input.Priority,
            Type = input.Type,
            EstimateMinutes = CleanEstimate(input.EstimateMinutes),
            AssigneeId = assignee?.Id,
            ParentTaskId = parent?.Id,
            StartDate = input.StartDate,
            DueDate = input.DueDate,
            SprintId = input.SprintId,
            Source = source,
            CreatedById = actor.UserId,
            CreatedAt = now,
            UpdatedAt = now,
            IdempotencyKey = Clean(input.IdempotencyKey)
        };
        ApplyStatus(task, status, now);

        db.Tasks.Add(task);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Created, departmentId, task.Title, new
        {
            task.Number, task.Title, task.Status, task.Priority, task.Type, task.EstimateMinutes, task.ProjectId, task.DepartmentId, task.AssigneeId,
            task.ParentTaskId, task.StartDate, task.DueDate, task.Source, task.SprintId, task.AssetId
        });
        await db.SaveChangesAsync(ct);

        if (assignee is not null && assignee.Id != actor.UserId)
            await notifications.TaskAssignedAsync(task, assignee, actor, ct);

        return await GetAsync(task.Id, ct);
    }

    /// <summary>Full edit: the input is the complete new state (null assignee/due date/sprint clears them).</summary>
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

        var assignee = await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
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

        var previousAssigneeId = task.AssigneeId;
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
            .Track("assigneeId", task.AssigneeId, assignee?.Id)
            .Track("parentTaskId", task.ParentTaskId, parent?.Id)
            .Track("startDate", task.StartDate, input.StartDate)
            .Track("dueDate", task.DueDate, input.DueDate)
            .Track("status", task.Status, newStatus)
            .Track("sprintId", task.SprintId, input.SprintId);

        if (!changes.HasChanges) return task;

        var now = DateTime.UtcNow;
        task.Title = title;
        task.Description = Clean(input.Description);
        task.DepartmentId = departmentId;
        task.ProjectId = input.ProjectId;
        task.AssetId = input.AssetId;
        task.Priority = input.Priority;
        task.Type = input.Type;
        task.EstimateMinutes = estimate;
        task.AssigneeId = assignee?.Id;
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

        if (assignee is not null && assignee.Id != previousAssigneeId && assignee.Id != actor.UserId)
            await notifications.TaskAssignedAsync(task, assignee, actor, ct);

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
        return task;
    }

    /// <summary>Quick inline assignee change (the task list control). Same rights as a full edit.</summary>
    public async Task<TaskItem> ChangeAssigneeAsync(Guid id, Guid? assigneeId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.Include(t => t.Assignee).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        // Taking an unassigned task for yourself (§6.5) needs no edit rights, only tasks.take in its department.
        var taking = assigneeId is not null && assigneeId == actor.UserId && AccessPolicy.CanTakeTask(actor, task);
        AccessPolicy.Require(taking || AccessPolicy.CanEditTask(actor, task), "You don't have permission to edit this task.");
        if (task.AssigneeId == assigneeId) return task;

        var assignee = await ValidateAssigneeAsync(assigneeId, task.DepartmentId, ct);
        var changes = new ChangeSet().Track("assigneeId", task.AssigneeId, assignee?.Id);
        task.AssigneeId = assignee?.Id;
        task.Assignee = assignee;
        task.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Updated, task.DepartmentId, task.Title, changes.Changes);
        await db.SaveChangesAsync(ct);

        if (assignee is not null && assignee.Id != actor.UserId)
            await notifications.TaskAssignedAsync(task, assignee, actor, ct);
        return task;
    }

    /// <summary>
    /// Take an unassigned task (§6.5): assign an open, unassigned task within the actor's tasks.take reach to the actor - the
    /// one assignee change someone may make on a task they can't otherwise edit. First come, first served: the update only
    /// lands while the task is still unassigned, so two people taking it at the same moment can't both win.
    /// </summary>
    public async Task<TaskItem> TakeAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) throw new ForbiddenException("Only a signed-in user can take a task.");
        var task = await db.Tasks.AsNoTracking().Include(t => t.Assignee).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        if (task.AssigneeId == me) return await GetAsync(id, ct);
        if (task.AssigneeId is not null)
            throw new ValidationException($"\"{task.Title}\" is already assigned to {task.Assignee!.DisplayName}.");
        if (!task.IsOpen) throw new ValidationException("A closed task can't be taken.");
        AccessPolicy.Require(AccessPolicy.CanTakeTask(actor, task), "You don't have permission to take tasks in this department.");
        await ValidateAssigneeAsync(me, task.DepartmentId, ct);

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var taken = await db.Tasks.Where(t => t.Id == id && t.AssigneeId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeId, me).SetProperty(t => t.UpdatedAt, now), ct);
        if (taken == 0)
        {
            var by = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => t.Assignee!.DisplayName).FirstOrDefaultAsync(ct);
            throw new ValidationException($"\"{task.Title}\" was just taken by {by ?? "someone else"}.");
        }
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Updated, task.DepartmentId, task.Title,
            new ChangeSet().Track("assigneeId", (Guid?)null, me).Changes);
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
        var task = await db.Tasks.Include(t => t.Assignee).FirstOrDefaultAsync(t => t.Id == id, ct)
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
            .ThenBy(t => t.Assignee == null).ThenBy(t => t.Assignee!.DisplayName)
            .ThenByDescending(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ToListAsync(ct);

        // Whatever is still open on the most recent earlier plan - "what didn't get finished last time".
        var leftOver = scoped.Where(t => t.PlannedFor != null && t.PlannedFor < date
            && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        var previous = await leftOver.MaxAsync(t => t.PlannedFor, ct);
        List<TaskItem> unfinished = previous is null ? [] : await leftOver.Where(t => t.PlannedFor == previous)
            .OrderBy(t => t.Assignee == null).ThenBy(t => t.Assignee!.DisplayName).ThenByDescending(t => t.Priority)
            .ToListAsync(ct);
        return new DayPlan { Date = date, Planned = planned, PreviousDate = previous, Unfinished = unfinished };
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

    /// <summary>Assignees must be active users in the task's department, or users whose role sees tasks everywhere (tasks.view at All).</summary>
    private async Task<ApplicationUser?> ValidateAssigneeAsync(Guid? assigneeId, Guid departmentId, CancellationToken ct)
    {
        if (assigneeId is not Guid id) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new NotFoundException("Assignee not found.");
        if (!user.IsActive || user.IsSystemAccount)
            throw new ValidationException("The assignee must be an active user.");
        if (user.DepartmentId != departmentId && !await RoleResolver.HasScopeAsync(db, user.Id, Permission.TasksView, PermissionScope.All, ct))
            throw new ValidationException("A task can't be assigned to a user outside its own department.");
        return user;
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

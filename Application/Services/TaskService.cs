using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// All task reads and writes for both the Razor Pages UI and the MCP tools. Department scoping and
/// the Member/DepartmentAdmin/SystemAdmin close rule are enforced here, not in the UI.
/// </summary>
public sealed class TaskService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    NotificationService notifications)
{
    private static IQueryable<TaskItem> WithIncludes(IQueryable<TaskItem> q) => q
        .Include(t => t.Department)
        .Include(t => t.Project).ThenInclude(p => p!.Department)
        .Include(t => t.Assignee)
        .Include(t => t.CreatedBy)
        .Include(t => t.Sprint)
        .Include(t => t.RecurringTaskDefinition);

    public async Task<PagedResult<TaskItem>> ListAsync(TaskFilter f, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = WithIncludes(db.Tasks.AsNoTracking());
        q = Scope(q, actor, f);

        if (f.ProjectId is Guid projectId) q = q.Where(t => t.ProjectId == projectId);
        if (f.Status is TaskItemStatus status) q = q.Where(t => t.Status == status);
        if (f.AssigneeId is Guid assigneeId) q = q.Where(t => t.AssigneeId == assigneeId);
        if (f.Priority is TaskPriority priority) q = q.Where(t => t.Priority == priority);
        if (f.Source is TaskSource source) q = q.Where(t => t.Source == source);
        if (f.DueBefore is DateOnly before) q = q.Where(t => t.DueDate != null && t.DueDate <= before);
        if (f.DueAfter is DateOnly after) q = q.Where(t => t.DueDate != null && t.DueDate >= after);
        if (f.SprintId is Guid sprintId) q = q.Where(t => t.SprintId == sprintId);
        if (f.RecurringTaskDefinitionId is Guid defId) q = q.Where(t => t.RecurringTaskDefinitionId == defId);
        if (f.BacklogOnly) q = q.Where(t => t.SprintId == null);
        if (f.OpenOnly) q = q.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = $"%{f.Search.Trim()}%";
            q = q.Where(t => EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Description ?? "", pattern));
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

    /// <summary>Department scoping. Non-admins only widen to all departments on the company-wide backlog/sprint views.</summary>
    private static IQueryable<TaskItem> Scope(IQueryable<TaskItem> q, Actor actor, TaskFilter f)
    {
        if (actor.IsSystemAdmin)
            return f.DepartmentId is Guid d ? q.Where(t => t.DepartmentId == d) : q;

        var companyWideView = f.AllDepartments && (f.BacklogOnly || f.SprintId != null);
        if (companyWideView)
            return f.DepartmentId is Guid d ? q.Where(t => t.DepartmentId == d) : q;

        return q.Where(t => t.DepartmentId == actor.DepartmentId);
    }

    public async Task<TaskItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await WithIncludes(db.Tasks).FirstOrDefaultAsync(t => t.Id == id, ct)
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
        AccessPolicy.Require(actor.CanAccessDepartment(departmentId), "You can only create tasks in your own department.");
        await RequireOpenDepartmentAsync(departmentId, ct);

        var assignee = await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
        await ValidateSprintAsync(input.SprintId, ct);

        var status = input.Status ?? TaskItemStatus.Todo;
        AccessPolicy.Require(!status.IsClosed() || actor.IsAdminFor(departmentId),
            "Only a Department Admin or System Admin can close a task.");

        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Title = title,
            Description = Clean(input.Description),
            DepartmentId = departmentId,
            ProjectId = input.ProjectId,
            Priority = input.Priority,
            AssigneeId = assignee?.Id,
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
            task.Title, task.Status, task.Priority, task.ProjectId, task.DepartmentId, task.AssigneeId, task.DueDate, task.Source, task.SprintId
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
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task),
            "Members can only edit tasks they created or are assigned to.");

        var title = RequireTitle(input.Title);
        var departmentId = await ResolveDepartmentAsync(input.ProjectId, input.DepartmentId, actor, task, ct);
        if (departmentId != task.DepartmentId)
        {
            AccessPolicy.Require(actor.CanAccessDepartment(departmentId), "You can't move a task to another department.");
            await RequireOpenDepartmentAsync(departmentId, ct);
        }

        var assignee = await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
        var newStatus = input.Status ?? task.Status;
        if (newStatus != task.Status)
            AccessPolicy.Require(AccessPolicy.CanChangeStatus(actor, task, newStatus),
                "Only a Department Admin or System Admin can close or reopen a task.");
        if (input.SprintId != task.SprintId)
        {
            AccessPolicy.Require(AccessPolicy.CanPlanTask(actor, task), "You can't plan tasks from another department.");
            await ValidateSprintAsync(input.SprintId, ct);
        }

        var previousAssigneeId = task.AssigneeId;
        var changes = new ChangeSet()
            .TrackText("title", task.Title, title)
            .TrackText("description", task.Description, input.Description)
            .Track("departmentId", task.DepartmentId, departmentId)
            .Track("projectId", task.ProjectId, input.ProjectId)
            .Track("priority", task.Priority, input.Priority)
            .Track("assigneeId", task.AssigneeId, assignee?.Id)
            .Track("dueDate", task.DueDate, input.DueDate)
            .Track("status", task.Status, newStatus)
            .Track("sprintId", task.SprintId, input.SprintId);

        if (!changes.HasChanges) return task;

        var now = DateTime.UtcNow;
        task.Title = title;
        task.Description = Clean(input.Description);
        task.DepartmentId = departmentId;
        task.ProjectId = input.ProjectId;
        task.Priority = input.Priority;
        task.AssigneeId = assignee?.Id;
        if (changes.Contains("dueDate")) task.DueSoonNotifiedAt = null; // a new due date earns a fresh reminder
        task.DueDate = input.DueDate;
        task.SprintId = input.SprintId;
        if (newStatus != task.Status) ApplyStatus(task, newStatus, now);
        task.UpdatedAt = now;

        var action = changes.Contains("status")
            ? (newStatus == TaskItemStatus.Done ? AuditAction.Completed : AuditAction.StatusChanged)
            : AuditAction.Updated;
        audit.Add(actor, AuditEntity.Task, task.Id, action, departmentId, task.Title, changes.Changes);
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
        AccessPolicy.Require(AccessPolicy.CanChangeStatus(actor, task, status),
            "Only a Department Admin or System Admin can close or reopen a task.");
        if (task.Status == status) return task;

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
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "Members can only edit tasks they created or are assigned to.");
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

    /// <summary>Quick inline due-date change (the task list control). Same rights as a full edit.</summary>
    public async Task<TaskItem> ChangeDueDateAsync(Guid id, DateOnly? dueDate, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "Members can only edit tasks they created or are assigned to.");
        if (task.DueDate == dueDate) return task;

        var changes = new ChangeSet().Track("dueDate", task.DueDate, dueDate);
        task.DueDate = dueDate;
        task.DueSoonNotifiedAt = null; // a new due date earns a fresh reminder
        task.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Updated, task.DepartmentId, task.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return task;
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
    /// <item>A department other than the project's makes it a cross-department project task. Only a System Admin
    /// may introduce that pairing; once filed, that department works the task like any other of theirs.</item>
    /// </list>
    /// Non-admins may only file tasks under their own department's projects.
    /// </summary>
    private async Task<Guid> ResolveDepartmentAsync(
        Guid? projectId, Guid? requestedDepartmentId, Actor actor, TaskItem? existing, CancellationToken ct)
    {
        if (projectId is not Guid pid)
        {
            return requestedDepartmentId ?? existing?.DepartmentId ?? actor.DepartmentId
                ?? throw new ValidationException("A department is required (System Admins aren't scoped to one, so choose it explicitly).");
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct)
            ?? throw new NotFoundException("Project not found.");
        var sameProject = existing?.ProjectId == pid;
        if (!sameProject)
        {
            if (project.Status == ProjectStatus.Archived)
                throw new ValidationException("Tasks can't be added to an archived project.");
            AccessPolicy.Require(AccessPolicy.CanAddTaskToProject(actor, project),
                "You can only add tasks to projects in your own department.");
        }

        var departmentId = requestedDepartmentId ?? (sameProject ? existing!.DepartmentId : project.DepartmentId);
        if (departmentId == project.DepartmentId) return departmentId;

        var alreadyFiledThere = sameProject && existing!.DepartmentId == departmentId;
        if (!alreadyFiledThere)
            AccessPolicy.Require(AccessPolicy.CanFileCrossDepartmentTask(actor),
                "Only a System Admin can file a task under a project owned by another department.");
        return departmentId;
    }

    private async Task RequireOpenDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
    }

    /// <summary>Assignees must be active users in the task's department (System Admins, who have no department, are always assignable).</summary>
    private async Task<ApplicationUser?> ValidateAssigneeAsync(Guid? assigneeId, Guid departmentId, CancellationToken ct)
    {
        if (assigneeId is not Guid id) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new NotFoundException("Assignee not found.");
        if (!user.IsActive || user.IsSystemAccount)
            throw new ValidationException("The assignee must be an active user.");
        if (user.DepartmentId != departmentId && !await IsSystemAdminAsync(user.Id, ct))
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

    private Task<bool> IsSystemAdminAsync(Guid userId, CancellationToken ct) =>
        db.UserRoles.Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .AnyAsync(name => name == Roles.SystemAdmin, ct);
}

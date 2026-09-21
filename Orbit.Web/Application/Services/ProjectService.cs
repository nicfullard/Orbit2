using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

public sealed class ProjectService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    /// <summary>
    /// Projects the caller may see: every project for a System Admin; otherwise the caller's own department's
    /// projects plus any project from another department that has tasks filed in the caller's department (§6.2.1).
    /// </summary>
    public async Task<IReadOnlyList<ProjectListItem>> ListAsync(ProjectFilter f, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = db.Projects.AsNoTracking().Include(p => p.Department).Include(p => p.Owner).AsQueryable();

        if (!actor.IsSystemAdmin)
            q = q.Where(p => p.DepartmentId == actor.DepartmentId || p.Tasks.Any(t => t.DepartmentId == actor.DepartmentId));
        else if (f.DepartmentId is Guid dept)
            q = q.Where(p => p.DepartmentId == dept);

        if (f.Status is ProjectStatus status)
            q = q.Where(p => p.Status == status);
        else if (!f.IncludeArchived)
            q = q.Where(p => p.Status != ProjectStatus.Archived);

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = $"%{f.Search.Trim()}%";
            q = q.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        var rows = await q.OrderBy(p => p.Name).Select(p => new
        {
            Project = p,
            Total = p.Tasks.Count(),
            Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
            Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
        }).ToListAsync(ct);

        return rows.Select(r => new ProjectListItem(r.Project, r.Total, r.Open, r.Done)).ToList();
    }

    /// <summary>
    /// Projects the caller may file tasks under (for pickers): any open project for a System Admin, otherwise the
    /// caller's own department's. <paramref name="includeProjectId"/> adds one specific project regardless, so an
    /// edit form can keep a task on the project it is already on (e.g. a cross-department task, §6.2.1).
    /// </summary>
    public async Task<IReadOnlyList<Project>> ListOpenForPickerAsync(Guid? departmentId = null, Guid? includeProjectId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = db.Projects.AsNoTracking().Include(p => p.Department)
            .Where(p => p.Status != ProjectStatus.Archived);
        if (!actor.IsSystemAdmin) q = q.Where(p => p.DepartmentId == actor.DepartmentId);
        else if (departmentId is Guid d) q = q.Where(p => p.DepartmentId == d);
        var list = await q.OrderBy(p => p.Department.Name).ThenBy(p => p.Name).ToListAsync(ct);
        if (includeProjectId is Guid keep && list.All(p => p.Id != keep))
        {
            var current = await db.Projects.AsNoTracking().Include(p => p.Department).FirstOrDefaultAsync(p => p.Id == keep, ct);
            if (current is not null) list.Add(current);
        }
        return list;
    }

    /// <summary>The department a project belongs to, for defaulting a new task's department; null if the project doesn't exist.</summary>
    public async Task<Guid?> GetDepartmentIdAsync(Guid projectId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.DepartmentId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Project detail with all of its tasks. Visible to the project's department, to a System Admin, and (read-only)
    /// to any department that has tasks filed under it. Tasks from other departments are listed but the per-task
    /// rules (<see cref="AccessPolicy.CanViewTask"/> etc.) still decide what the caller can open or change.
    /// </summary>
    public async Task<Project> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.AsNoTracking()
            .Include(p => p.Department)
            .Include(p => p.Owner)
            .Include(p => p.Tasks).ThenInclude(t => t.Department)
            .Include(p => p.Tasks).ThenInclude(t => t.Assignee)
            .Include(p => p.Tasks).ThenInclude(t => t.Sprint)
            .Include(p => p.Tasks).ThenInclude(t => t.ParentTask)
            .Include(p => p.RecurringTaskDefinitions).ThenInclude(r => r.Department)
            .Include(p => p.RecurringTaskDefinitions).ThenInclude(r => r.Assignee)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Project not found.");
        var shared = actor.DepartmentId is Guid d && project.Tasks.Any(t => t.DepartmentId == d);
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project, shared), "This project belongs to another department.");
        return project;
    }

    public async Task<ProjectStatusSummary> GetStatusAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.AsNoTracking().Include(p => p.Department).Include(p => p.Owner)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Project not found.");
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project, await IsSharedWithAsync(id, actor, ct)),
            "This project belongs to another department.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var minutes = await db.TimeEntries.Where(e => e.Task.ProjectId == id).SumAsync(e => (int?)e.DurationMinutes, ct) ?? 0;
        var estimated = await db.Tasks.Where(t => t.ProjectId == id && t.Status != TaskItemStatus.Cancelled)
            .SumAsync(t => t.EstimateMinutes, ct) ?? 0;
        // One grouped query gives the per-department breakdown; the project-wide counts are its sums.
        var byDepartment = (await db.Tasks.Where(t => t.ProjectId == id)
            .GroupBy(t => new { t.DepartmentId, t.Department.Name })
            .Select(g => new
            {
                g.Key.DepartmentId,
                g.Key.Name,
                Todo = g.Count(t => t.Status == TaskItemStatus.Todo),
                InProgress = g.Count(t => t.Status == TaskItemStatus.InProgress),
                Blocked = g.Count(t => t.Status == TaskItemStatus.Blocked),
                Done = g.Count(t => t.Status == TaskItemStatus.Done),
                Cancelled = g.Count(t => t.Status == TaskItemStatus.Cancelled),
                Overdue = g.Count(t => t.DueDate != null && t.DueDate < today
                    && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
            })
            .OrderBy(d => d.Name)
            .ToListAsync(ct))
            .Select(d => new DepartmentTaskCount(d.DepartmentId, d.Name, d.Todo, d.InProgress, d.Blocked, d.Done, d.Cancelled, d.Overdue))
            .ToList();

        return new ProjectStatusSummary(project.Id, project.Name, project.Status, project.Department.Name, project.DepartmentId,
            project.Owner.DisplayName, project.TargetDate, project.RequiredBufferWorkingDays, byDepartment.Sum(d => d.Total),
            byDepartment.Sum(d => d.Todo), byDepartment.Sum(d => d.InProgress), byDepartment.Sum(d => d.Blocked),
            byDepartment.Sum(d => d.Done), byDepartment.Sum(d => d.Cancelled), byDepartment.Sum(d => d.Overdue), minutes, estimated,
            byDepartment);
    }

    public async Task<Project> CreateAsync(ProjectInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var name = RequireName(input.Name);
        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (System Admins aren't scoped to one, so choose it explicitly).");
        AccessPolicy.Require(actor.CanAccessDepartment(departmentId), "You can only create projects in your own department.");
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");

        var ownerId = input.OwnerId ?? actor.UserId
            ?? throw new ValidationException("An owner is required.");
        await ValidateOwnerAsync(ownerId, departmentId, ct);

        var now = DateTime.UtcNow;
        var project = new Project
        {
            Name = name,
            Description = Clean(input.Description),
            DepartmentId = departmentId,
            Status = input.Status,
            OwnerId = ownerId,
            TargetDate = input.TargetDate,
            RequiredBufferWorkingDays = CleanBuffer(input.RequiredBufferWorkingDays),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Projects.Add(project);
        audit.Add(actor, AuditEntity.Project, project.Id, AuditAction.Created, departmentId, project.Name,
            new { project.Name, project.Status, project.OwnerId, project.TargetDate, project.RequiredBufferWorkingDays });
        await db.SaveChangesAsync(ct);
        return await GetAsync(project.Id, ct);
    }

    public async Task<Project> UpdateAsync(Guid id, ProjectInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.Include(p => p.Department).Include(p => p.Owner)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Project not found.");
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project), "This project belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditProject(actor, project), "Members can only edit projects they own.");

        var name = RequireName(input.Name);
        var departmentId = input.DepartmentId ?? project.DepartmentId;
        if (departmentId != project.DepartmentId)
        {
            AccessPolicy.Require(actor.IsSystemAdmin, "Only a System Admin can move a project to another department.");
            var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
                ?? throw new NotFoundException("Department not found.");
            if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        }
        var ownerId = input.OwnerId ?? project.OwnerId;
        await ValidateOwnerAsync(ownerId, departmentId, ct);

        var buffer = CleanBuffer(input.RequiredBufferWorkingDays);
        var changes = new ChangeSet()
            .TrackText("name", project.Name, name)
            .TrackText("description", project.Description, input.Description)
            .Track("departmentId", project.DepartmentId, departmentId)
            .Track("status", project.Status, input.Status)
            .Track("ownerId", project.OwnerId, ownerId)
            .Track("targetDate", project.TargetDate, input.TargetDate)
            .Track("requiredBufferWorkingDays", project.RequiredBufferWorkingDays, buffer);
        if (!changes.HasChanges) return project;

        project.Name = name;
        project.Description = Clean(input.Description);
        project.Status = input.Status;
        project.OwnerId = ownerId;
        project.TargetDate = input.TargetDate;
        project.RequiredBufferWorkingDays = buffer;
        project.UpdatedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (changes.Contains("departmentId"))
        {
            // Tasks and definitions that sat in the project's own department move with it. Cross-department ones
            // (§6.2.1) were filed in their department deliberately and keep it.
            var previous = project.DepartmentId;
            project.DepartmentId = departmentId;
            await db.Tasks.Where(t => t.ProjectId == id && t.DepartmentId == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DepartmentId, departmentId), ct);
            await db.RecurringTaskDefinitions.Where(r => r.ProjectId == id && r.DepartmentId == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DepartmentId, departmentId), ct);
        }

        audit.Add(actor, AuditEntity.Project, project.Id,
            input.Status == ProjectStatus.Archived && changes.Contains("status") ? AuditAction.Archived : AuditAction.Updated,
            departmentId, project.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(project.Id, ct);
    }

    /// <summary>Soft archive: tasks are kept.</summary>
    public async Task ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Project not found.");
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project), "This project belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditProject(actor, project), "Members can only archive projects they own.");
        if (project.Status == ProjectStatus.Archived) return;

        var previous = project.Status;
        project.Status = ProjectStatus.Archived;
        project.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Project, project.Id, AuditAction.Archived, project.DepartmentId, project.Name,
            new { status = new { from = previous, to = ProjectStatus.Archived } });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when the caller's department has tasks filed under the project (§6.2.1 shared visibility).</summary>
    private async Task<bool> IsSharedWithAsync(Guid projectId, Actor actor, CancellationToken ct) =>
        actor.DepartmentId is Guid d && await db.Tasks.AnyAsync(t => t.ProjectId == projectId && t.DepartmentId == d, ct);

    private static string RequireName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("Name is required.");
        if (n.Length > 200) throw new ValidationException("Name must be 200 characters or fewer.");
        return n;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The required project buffer (§6.17) is whole working days: null or 0 clears it; at most a year of working days.</summary>
    private static int? CleanBuffer(int? days)
    {
        if (days is null or 0) return null;
        if (days < 0) throw new ValidationException("The required project buffer can't be negative.");
        if (days > 260) throw new ValidationException("The required project buffer must be at most 260 working days.");
        return days;
    }

    private async Task ValidateOwnerAsync(Guid ownerId, Guid departmentId, CancellationToken ct)
    {
        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct)
            ?? throw new NotFoundException("Owner not found.");
        if (!owner.IsActive) throw new ValidationException("The owner must be an active user.");
        if (owner.IsSystemAccount) return; // API-created projects default to the Claude agent as owner.
        if (owner.DepartmentId == departmentId) return;
        var isSystemAdmin = await db.UserRoles.Where(ur => ur.UserId == ownerId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .AnyAsync(n => n == Roles.SystemAdmin, ct);
        if (!isSystemAdmin) throw new ValidationException("The owner must belong to the project's department.");
    }
}

using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Recurring task definitions (RRULE-based) and the generation of their task instances.</summary>
public sealed class RecurrenceService(
    NumberingService numbering,
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    ILogger<RecurrenceService> logger)
{
    // --- RRULE evaluation ------------------------------------------------------------------

    /// <summary>Validates and normalises an RRULE string such as "FREQ=WEEKLY;BYDAY=MO".</summary>
    public static string ValidateRule(string? rule)
    {
        var text = rule?.Trim().TrimEnd(';') ?? string.Empty;
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        text = text.ToUpperInvariant();
        if (!text.Contains("FREQ=")) throw new ValidationException("The recurrence rule must include FREQ (e.g. FREQ=WEEKLY;BYDAY=MO).");
        try
        {
            _ = new RecurrencePattern(text);
        }
        catch (Exception ex)
        {
            throw new ValidationException($"Invalid recurrence rule: {ex.Message}");
        }
        // Make sure the rule actually produces something.
        try
        {
            _ = Occurrences(text, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow)).Take(1).ToList();
        }
        catch (ValidationException) { throw; }
        catch (Exception ex)
        {
            throw new ValidationException($"The recurrence rule can't be evaluated: {ex.Message}");
        }
        return text;
    }

    /// <summary>Lazily enumerates occurrence dates on or after <paramref name="from"/>. Always bound the sequence with Take/TakeWhile.</summary>
    public static IEnumerable<DateOnly> Occurrences(string rule, DateOnly start, DateOnly from)
    {
        var evt = new CalendarEvent
        {
            DtStart = new CalDateTime(start),
            RecurrenceRule = new RecurrencePattern(rule)
        };
        var searchFrom = from < start ? start : from;
        return evt.GetOccurrences(new CalDateTime(searchFrom)).Select(o => o.Period.StartTime.Date);
    }

    public static DateOnly? NextOccurrenceOnOrAfter(string rule, DateOnly start, DateOnly date)
    {
        foreach (var d in Occurrences(rule, start, date).Take(1)) return d;
        return null;
    }

    public static DateOnly? NextOccurrenceAfter(string rule, DateOnly start, DateOnly date) =>
        NextOccurrenceOnOrAfter(rule, start, date.AddDays(1));

    public static IReadOnlyList<DateOnly> Upcoming(RecurringTaskDefinition def, int count, DateOnly? from = null)
    {
        var start = from ?? def.NextRunDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        try
        {
            return Occurrences(def.RecurrenceRule, def.StartDate, start).Take(Math.Clamp(count, 1, 100)).ToList();
        }
        catch
        {
            return [];
        }
    }

    // --- definitions CRUD ---------------------------------------------------------------------

    private static IQueryable<RecurringTaskDefinition> WithIncludes(IQueryable<RecurringTaskDefinition> q) => q
        .Include(r => r.Department).Include(r => r.Project).ThenInclude(p => p!.Department).Include(r => r.Assignee).Include(r => r.CreatedBy);

    public async Task<IReadOnlyList<RecurringTaskDefinition>> ListAsync(RecurringFilter f, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = WithIncludes(db.RecurringTaskDefinitions.AsNoTracking());
        if (!actor.IsSystemAdmin) q = q.Where(r => r.DepartmentId == actor.DepartmentId);
        else if (f.DepartmentId is Guid dept) q = q.Where(r => r.DepartmentId == dept);
        if (f.ProjectId is Guid project) q = q.Where(r => r.ProjectId == project);
        if (!f.IncludeInactive) q = q.Where(r => r.Active);
        return await q.OrderByDescending(r => r.Active).ThenBy(r => r.NextRunDate == null).ThenBy(r => r.NextRunDate).ThenBy(r => r.Title).ToListAsync(ct);
    }

    public async Task<RecurringTaskDefinition> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var def = await WithIncludes(db.RecurringTaskDefinitions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Recurring task definition not found.");
        AccessPolicy.Require(actor.CanAccessDepartment(def.DepartmentId), "This definition belongs to another department.");
        return def;
    }

    public async Task<RecurringTaskDefinition> CreateAsync(RecurringInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var title = RequireTitle(input.Title);
        var rule = ValidateRule(input.RecurrenceRule);
        var departmentId = await ResolveDepartmentAsync(input.ProjectId, input.DepartmentId, actor, existing: null, ct);
        AccessPolicy.Require(actor.CanAccessDepartment(departmentId), "You can only create recurring tasks in your own department.");
        await RequireOpenDepartmentAsync(departmentId, ct);
        await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
        if (input.LeadTimeDays is < 0 or > 365) throw new ValidationException("Lead time must be between 0 and 365 days.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        var def = new RecurringTaskDefinition
        {
            Title = title,
            Description = Clean(input.Description),
            ProjectId = input.ProjectId,
            DepartmentId = departmentId,
            Priority = input.Priority,
            AssigneeId = input.AssigneeId,
            RecurrenceRule = rule,
            StartDate = input.StartDate,
            LeadTimeDays = input.LeadTimeDays,
            Active = true,
            CreatedById = actor.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        def.NextRunDate = NextOccurrenceOnOrAfter(rule, def.StartDate, today > def.StartDate ? today : def.StartDate);
        if (def.NextRunDate is null)
            throw new ValidationException("The recurrence rule produces no future occurrences from the start date.");

        db.RecurringTaskDefinitions.Add(def);
        audit.Add(actor, AuditEntity.RecurringTaskDefinition, def.Id, AuditAction.Created, departmentId, def.Title,
            new { def.Title, def.RecurrenceRule, def.StartDate, def.NextRunDate, def.LeadTimeDays, def.ProjectId, def.DepartmentId, def.AssigneeId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(def.Id, ct);
    }

    public async Task<RecurringTaskDefinition> UpdateAsync(Guid id, RecurringInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var def = await WithIncludes(db.RecurringTaskDefinitions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Recurring task definition not found.");
        AccessPolicy.Require(actor.CanAccessDepartment(def.DepartmentId), "This definition belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditRecurring(actor, def), "Members can only edit recurring tasks they created or are assigned to.");

        var title = RequireTitle(input.Title);
        var rule = ValidateRule(input.RecurrenceRule);
        var departmentId = await ResolveDepartmentAsync(input.ProjectId, input.DepartmentId, actor, def, ct);
        if (departmentId != def.DepartmentId)
        {
            AccessPolicy.Require(actor.CanAccessDepartment(departmentId), "You can't move a definition to another department.");
            await RequireOpenDepartmentAsync(departmentId, ct);
        }
        await ValidateAssigneeAsync(input.AssigneeId, departmentId, ct);
        if (input.LeadTimeDays is < 0 or > 365) throw new ValidationException("Lead time must be between 0 and 365 days.");

        var changes = new ChangeSet()
            .TrackText("title", def.Title, title)
            .TrackText("description", def.Description, input.Description)
            .Track("projectId", def.ProjectId, input.ProjectId)
            .Track("departmentId", def.DepartmentId, departmentId)
            .Track("priority", def.Priority, input.Priority)
            .Track("assigneeId", def.AssigneeId, input.AssigneeId)
            .Track("recurrenceRule", def.RecurrenceRule, rule)
            .Track("startDate", def.StartDate, input.StartDate)
            .Track("leadTimeDays", def.LeadTimeDays, input.LeadTimeDays);
        if (!changes.HasChanges) return def;

        def.Title = title;
        def.Description = Clean(input.Description);
        def.ProjectId = input.ProjectId;
        def.DepartmentId = departmentId;
        def.Priority = input.Priority;
        def.AssigneeId = input.AssigneeId;
        def.LeadTimeDays = input.LeadTimeDays;
        if (changes.Contains("recurrenceRule") || changes.Contains("startDate"))
        {
            def.RecurrenceRule = rule;
            def.StartDate = input.StartDate;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            def.NextRunDate = NextOccurrenceOnOrAfter(rule, def.StartDate, today > def.StartDate ? today : def.StartDate);
            if (def.NextRunDate is null)
                throw new ValidationException("The recurrence rule produces no future occurrences from the start date.");
        }
        def.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RecurringTaskDefinition, def.Id, AuditAction.Updated, departmentId, def.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return await GetAsync(def.Id, ct);
    }

    /// <summary>Pause (keeps history) or resume. Resuming recomputes NextRunDate from today.</summary>
    public async Task<RecurringTaskDefinition> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var def = await WithIncludes(db.RecurringTaskDefinitions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Recurring task definition not found.");
        AccessPolicy.Require(actor.CanAccessDepartment(def.DepartmentId), "This definition belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditRecurring(actor, def), "Members can only pause or resume recurring tasks they created or are assigned to.");
        if (def.Active == active) return def;

        def.Active = active;
        def.UpdatedAt = DateTime.UtcNow;
        if (active)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            def.NextRunDate = NextOccurrenceOnOrAfter(def.RecurrenceRule, def.StartDate, today);
            if (def.NextRunDate is null)
            {
                def.Active = false;
                throw new ValidationException("The recurrence rule produces no further occurrences; the definition can't be resumed.");
            }
        }
        audit.Add(actor, AuditEntity.RecurringTaskDefinition, def.Id, active ? AuditAction.Resumed : AuditAction.Paused,
            def.DepartmentId, def.Title, new { def.NextRunDate });
        await db.SaveChangesAsync(ct);
        return def;
    }

    /// <summary>Creates the next instance immediately, regardless of lead time (admin convenience / testing).</summary>
    public async Task<TaskItem> GenerateNowAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var def = await WithIncludes(db.RecurringTaskDefinitions).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Recurring task definition not found.");
        AccessPolicy.Require(actor.CanAccessDepartment(def.DepartmentId), "This definition belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditRecurring(actor, def), "You can't generate tasks for this definition.");
        if (def.NextRunDate is not DateOnly due)
            throw new ValidationException("This definition has no further occurrences.");

        var task = await CreateInstanceAsync(def, due, actor, ct)
            ?? throw new ValidationException($"A task for {due:yyyy-MM-dd} already exists.");
        Advance(def, due);
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <summary>
    /// The daily job: for each active definition whose next occurrence falls within its lead time,
    /// create the task instance (in the backlog) and advance NextRunDate. Occurrences that were
    /// missed entirely (already in the past) are skipped rather than generated as overdue noise.
    /// </summary>
    public async Task<int> GenerateDueTasksAsync(DateOnly today, CancellationToken ct = default)
    {
        var defs = await WithIncludes(db.RecurringTaskDefinitions)
            .Where(r => r.Active && r.NextRunDate != null).ToListAsync(ct);
        var created = 0;
        foreach (var def in defs)
        {
            var guard = 0;
            while (def.Active && def.NextRunDate is DateOnly due && due.AddDays(-def.LeadTimeDays) <= today && guard++ < 100)
            {
                if (due < today)
                {
                    logger.LogWarning("Skipping missed occurrence {Due} of recurring task {Title} ({Id})", due, def.Title, def.Id);
                }
                else if (await CreateInstanceAsync(def, due, Actor.System, ct) is not null)
                {
                    created++;
                }
                Advance(def, due);
            }
        }
        await db.SaveChangesAsync(ct);
        return created;
    }

    private void Advance(RecurringTaskDefinition def, DateOnly current)
    {
        try
        {
            def.NextRunDate = NextOccurrenceAfter(def.RecurrenceRule, def.StartDate, current);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not evaluate recurrence rule for definition {Id}; deactivating", def.Id);
            def.NextRunDate = null;
        }
        if (def.NextRunDate is null) def.Active = false;
        def.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Generated tasks inherit the definition's department (not the project's - §6.2.1) so each department gets its own instances.</summary>
    private async Task<TaskItem?> CreateInstanceAsync(RecurringTaskDefinition def, DateOnly due, Actor actor, CancellationToken ct)
    {
        var exists = await db.Tasks.AnyAsync(t => t.RecurringTaskDefinitionId == def.Id && t.DueDate == due, ct);
        if (exists) return null;

        var projectId = def.Project is { Status: not ProjectStatus.Archived } ? def.ProjectId : null;
        var assigneeId = def.Assignee is { IsActive: true, IsSystemAccount: false } ? def.AssigneeId : null;
        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Number = await numbering.NextAsync(NumberingService.TaskPrefix, now, ct),
            Title = def.Title,
            Description = def.Description,
            DepartmentId = def.DepartmentId,
            ProjectId = projectId,
            Priority = def.Priority,
            AssigneeId = assigneeId,
            DueDate = due,
            Status = TaskItemStatus.Todo,
            Source = TaskSource.Recurring,
            CreatedById = actor.UserId,
            RecurringTaskDefinitionId = def.Id,
            SprintId = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tasks.Add(task);
        def.LastGeneratedAt = now;
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.Generated, def.DepartmentId, task.Title,
            new { recurringTaskDefinitionId = def.Id, dueDate = due, task.Priority, task.AssigneeId, task.ProjectId });
        return task;
    }

    // --- helpers --------------------------------------------------------------------------------

    private static string RequireTitle(string? title)
    {
        var t = title?.Trim();
        if (string.IsNullOrEmpty(t)) throw new ValidationException("Title is required.");
        if (t.Length > 300) throw new ValidationException("Title must be 300 characters or fewer.");
        return t;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Same rule as <c>TaskService.ResolveDepartmentAsync</c> (§6.2.1): a definition on a project defaults to the
    /// project's department; only a System Admin may file it under a project owned by another department, and a
    /// definition already filed that way keeps its department on edit.
    /// </summary>
    private async Task<Guid> ResolveDepartmentAsync(
        Guid? projectId, Guid? requestedDepartmentId, Actor actor, RecurringTaskDefinition? existing, CancellationToken ct)
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
                throw new ValidationException("Recurring tasks can't be added to an archived project.");
            AccessPolicy.Require(AccessPolicy.CanAddTaskToProject(actor, project),
                "You can only add recurring tasks to projects in your own department.");
        }

        var departmentId = requestedDepartmentId ?? (sameProject ? existing!.DepartmentId : project.DepartmentId);
        if (departmentId == project.DepartmentId) return departmentId;

        var alreadyFiledThere = sameProject && existing!.DepartmentId == departmentId;
        if (!alreadyFiledThere)
            AccessPolicy.Require(AccessPolicy.CanFileCrossDepartmentTask(actor),
                "Only a System Admin can file a recurring task under a project owned by another department.");
        return departmentId;
    }

    private async Task RequireOpenDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (department.IsArchived) throw new ValidationException($"Department \"{department.Name}\" is archived.");
    }

    private async Task ValidateAssigneeAsync(Guid? assigneeId, Guid departmentId, CancellationToken ct)
    {
        if (assigneeId is not Guid id) return;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new NotFoundException("Assignee not found.");
        if (!user.IsActive || user.IsSystemAccount) throw new ValidationException("The assignee must be an active user.");
        if (user.DepartmentId == departmentId) return;
        var isSystemAdmin = await db.UserRoles.Where(ur => ur.UserId == id)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .AnyAsync(n => n == Roles.SystemAdmin, ct);
        if (!isSystemAdmin) throw new ValidationException("A task can't be assigned to a user outside its own department.");
    }
}

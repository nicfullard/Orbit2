using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Builds the tiered post-login dashboard (§6.9). The tier is the actor's tasks.view scope: Own = the personal
/// dashboard, Department = the department dashboard, All = the company-wide one. A role that may view tasks but edit none gets its
/// view scope without the widgets about its own work; a role that sees no tasks gets no task widgets at all.
/// </summary>
public sealed class DashboardService(ApplicationDbContext db, IActorProvider actors)
{
    public async Task<DashboardModel> BuildAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var tier = DashboardTier(actor);
        var canEdit = actor.Has(Permission.TasksEdit);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string? departmentName = actor.DepartmentId is Guid deptId
            ? await db.Departments.Where(d => d.Id == deptId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        if (tier == PermissionScope.None)
            return new DashboardModel
            {
                Actor = actor, Tier = tier, Today = today, DepartmentName = departmentName, ScopeLabel = "Your role can't see any tasks", CanEditTasks = canEdit
            };

        // The personal tier is "my work": assigned to me, rather than everything Own lets me see. Someone who can't edit tasks has no
        // work of their own there, so at Own they see what Own reaches - the tasks they raised, are assigned or are the requestee of.
        var personal = tier == PermissionScope.Own && canEdit;
        var me = actor.UserId;
        var dueHorizon = today.AddDays(7);

        var open = db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);

        IQueryable<TaskItem> InTier(IQueryable<TaskItem> q) => personal ? q.Where(t => t.Assignments.Any(x => x.UserId == me)) : Scoping.Tasks(q, actor);
        var scope = InTier(open);
        var scopeLabel = tier switch
        {
            PermissionScope.All => "Open tasks across the company",
            PermissionScope.Department => "Open tasks in your department",
            _ => personal ? "My open tasks" : "Open tasks you raised, requested or are assigned"
        };

        // Today's day plan (§6.12): same scope, but over all tasks rather than open ones so "done today" counts.
        var allInScope = InTier(db.Tasks.AsNoTracking());
        var plannedToday = await allInScope.CountAsync(t => t.PlannedFor == today, ct);
        var plannedTodayDone = await allInScope.CountAsync(t => t.PlannedFor == today && t.Status == TaskItemStatus.Done, ct);

        var statusCounts = await scope.GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(TaskItemStatus s) => statusCounts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        var overdue = await scope.CountAsync(t => t.DueDate != null && t.DueDate < today, ct);

        var openTasks = await Detailed(scope).Take(12).ToListAsync(ct);
        List<TaskItem> myOpen = personal ? openTasks
            : canEdit ? await Detailed(open.Where(t => t.Assignments.Any(x => x.UserId == me))).Take(8).ToListAsync(ct)
            : [];

        // Work nobody has picked up yet, which the viewer may take (§6.5). The wider tiers already list the whole department above.
        List<TaskItem> upForGrabs = [];
        var showUpForGrabs = personal && actor.DepartmentId is Guid ownDept && actor.CanInDepartment(Permission.TasksTake, ownDept);
        if (showUpForGrabs)
        {
            var dept = actor.DepartmentId;
            upForGrabs = await Detailed(open.Where(t => t.DepartmentId == dept && !t.Assignments.Any())).Take(8).ToListAsync(ct);
        }

        // Active sprint
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Status == SprintStatus.Active, ct);
        List<TaskItem> sprintTasks = [];
        List<DepartmentSprintProgress> sprintByDept = [];
        int sprintTotal = 0, sprintDone = 0;
        if (sprint is not null)
        {
            var all = db.Tasks.AsNoTracking().Where(t => t.SprintId == sprint.Id);
            sprintTotal = await all.CountAsync(ct);
            sprintDone = await all.CountAsync(t => t.Status == TaskItemStatus.Done, ct);
            sprintTasks = await InTier(all)
                .Include(t => t.Project).Include(t => t.Department).Include(t => t.Asset)
                .OrderBy(EnumOrder.ByTaskStatus).ThenByDescending(EnumOrder.ByTaskPriority).Take(20).ToListAsync(ct);
            if (tier == PermissionScope.All)
            {
                var rows = await all.GroupBy(t => t.Department.Name)
                    .Select(g => new { Name = g.Key, Total = g.Count(), Done = g.Count(t => t.Status == TaskItemStatus.Done) })
                    .OrderBy(x => x.Name).ToListAsync(ct);
                sprintByDept = rows.Select(r => new DepartmentSprintProgress(r.Name, r.Total, r.Done)).ToList();
            }
        }

        // Projects at a glance: the projects the viewer may see; on the personal tier only those with their own tasks.
        var projects = Scoping.Projects(db.Projects.AsNoTracking().Include(p => p.Department).Include(p => p.Owner)
            .Where(p => p.Status == ProjectStatus.Active), actor);
        if (personal) projects = projects.Where(p => p.Tasks.Any(t => t.Assignments.Any(x => x.UserId == me)));
        var projectRows = await projects.OrderBy(p => p.Name).Take(12).Select(p => new
        {
            Project = p,
            Total = p.Tasks.Count(),
            Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
            Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
        }).ToListAsync(ct);

        var dueSoon = await scope.Where(t => t.DueDate != null && t.DueDate <= dueHorizon)
            .Include(t => t.Project).Include(t => t.Department).Include(t => t.Asset)
            .OrderBy(t => t.DueDate).Take(10).ToListAsync(ct);

        List<NameCount> byAssignee = [];
        List<AuditLog> activity = [];
        List<DepartmentLoad> byDepartment = [];
        if (tier >= PermissionScope.Department)
        {
            // A task several people share counts under each of them (§6.2.3), so the rows can add up to more than the open tasks.
            var assigneeRows = await scope.SelectMany(t => t.Assignments)
                .GroupBy(x => new { x.UserId, x.User.DisplayName })
                .Select(g => new { Name = g.Key.DisplayName, Count = g.Count() })
                .ToListAsync(ct);
            var unassigned = await scope.CountAsync(t => !t.Assignments.Any(), ct);
            if (unassigned > 0) assigneeRows.Add(new { Name = "Unassigned", Count = unassigned });
            byAssignee = assigneeRows.OrderByDescending(r => r.Count).ThenBy(r => r.Name).Select(r => new NameCount(r.Name, r.Count)).ToList();

            var feed = db.AuditLogs.AsNoTracking().Where(a => a.EntityType == AuditEntity.Task);
            if (tier == PermissionScope.Department) feed = feed.Where(a => a.DepartmentId == actor.DepartmentId);
            activity = await feed.OrderByDescending(a => a.Timestamp).Take(15).ToListAsync(ct);
        }
        if (tier == PermissionScope.All)
        {
            var deptRows = await db.Departments.AsNoTracking().Where(d => !d.IsArchived).OrderBy(d => d.Name)
                .Select(d => new
                {
                    d.Id,
                    d.Name,
                    OpenTasks = d.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
                    ActiveProjects = d.Projects.Count(p => p.Status == ProjectStatus.Active),
                    Users = d.Users.Count(u => u.IsActive && !u.IsSystemAccount)
                })
                .ToListAsync(ct);
            byDepartment = deptRows.Select(r => new DepartmentLoad(r.Id, r.Name, r.OpenTasks, r.ActiveProjects, r.Users)).ToList();
        }

        return new DashboardModel
        {
            Actor = actor,
            Tier = tier,
            Today = today,
            DepartmentName = departmentName,
            ScopeLabel = scopeLabel,
            CanEditTasks = canEdit,
            TodoCount = Count(TaskItemStatus.Todo),
            InProgressCount = Count(TaskItemStatus.InProgress),
            WaitingCount = Count(TaskItemStatus.Waiting),
            BlockedCount = Count(TaskItemStatus.Blocked),
            OverdueCount = overdue,
            PlannedTodayCount = plannedToday,
            PlannedTodayDone = plannedTodayDone,
            OpenTasks = openTasks,
            MyOpenTasks = myOpen,
            UpForGrabs = upForGrabs,
            ShowUpForGrabs = showUpForGrabs,
            ActiveSprint = sprint,
            SprintTasks = sprintTasks,
            SprintTotal = sprintTotal,
            SprintDone = sprintDone,
            SprintByDepartment = sprintByDept,
            Projects = projectRows.Select(r => new ProjectListItem(r.Project, r.Total, r.Open, r.Done)).ToList(),
            DueSoon = dueSoon,
            OpenByAssignee = byAssignee,
            Activity = activity,
            ByDepartment = byDepartment
        };
    }

    /// <summary>
    /// The tier is the tasks.view scope (§6.9), with one refinement: a role that sees its department but may only edit
    /// its own tasks (tasks.edit at Own) gets the personal dashboard, since its own work is what it acts on - which keeps the
    /// shipped Member role on the dashboard it always had. A view-only role (<see cref="IsViewOnly"/>) has no work of its own,
    /// so it keeps its view scope.
    /// </summary>
    public static PermissionScope DashboardTier(Actor actor)
    {
        var tier = actor.ScopeOf(Permission.TasksView);
        if (tier == PermissionScope.Department && actor.ScopeOf(Permission.TasksEdit) == PermissionScope.Own)
            return PermissionScope.Own;
        return tier;
    }

    /// <summary>May view tasks but edit none: the dashboard shows what they may see, without the widgets about their own work.</summary>
    public static bool IsViewOnly(Actor actor) => actor.Has(Permission.TasksView) && !actor.Has(Permission.TasksEdit);

    private static IQueryable<TaskItem> Detailed(IQueryable<TaskItem> q) => q
        .Include(t => t.Project).Include(t => t.Department).Include(t => t.Asset)
        .OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenByDescending(EnumOrder.ByTaskPriority).ThenByDescending(t => t.CreatedAt);
}

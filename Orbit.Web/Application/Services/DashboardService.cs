using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Builds the tiered post-login dashboard (§6.9). The tier is the actor's tasks.view scope: Own = the personal
/// dashboard, Department = the department dashboard, All = the company-wide one; a role that sees no tasks gets an empty page.
/// </summary>
public sealed class DashboardService(ApplicationDbContext db, IActorProvider actors)
{
    public async Task<DashboardModel> BuildAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var tier = DashboardTier(actor);
        var personal = tier == PermissionScope.Own;
        var me = actor.UserId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dueHorizon = today.AddDays(7);

        var open = db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);

        // The personal tier is "my work": assigned to me, rather than everything Own lets me see.
        IQueryable<TaskItem> InTier(IQueryable<TaskItem> q) => personal ? q.Where(t => t.AssigneeId == me) : Scoping.Tasks(q, actor);
        var scope = InTier(open);
        var scopeLabel = tier switch
        {
            PermissionScope.All => "Open tasks across the company",
            PermissionScope.Department => "Open tasks in your department",
            PermissionScope.Own => "My open tasks",
            _ => "Your role can't see any tasks"
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
        var myOpen = personal
            ? openTasks
            : await Detailed(open.Where(t => t.AssigneeId == me)).Take(8).ToListAsync(ct);

        // Work nobody has picked up yet, which the viewer may take (§6.5). The wider tiers already list the whole department above.
        List<TaskItem> upForGrabs = [];
        var showUpForGrabs = personal && actor.DepartmentId is Guid ownDept && actor.CanInDepartment(Permission.TasksTake, ownDept);
        if (showUpForGrabs)
        {
            var dept = actor.DepartmentId;
            upForGrabs = await Detailed(open.Where(t => t.DepartmentId == dept && t.AssigneeId == null)).Take(8).ToListAsync(ct);
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
                .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department).Include(t => t.Asset)
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
        if (personal) projects = projects.Where(p => p.Tasks.Any(t => t.AssigneeId == me));
        var projectRows = await projects.OrderBy(p => p.Name).Take(12).Select(p => new
        {
            Project = p,
            Total = p.Tasks.Count(),
            Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
            Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
        }).ToListAsync(ct);

        var dueSoon = await scope.Where(t => t.DueDate != null && t.DueDate <= dueHorizon)
            .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department).Include(t => t.Asset)
            .OrderBy(t => t.DueDate).Take(10).ToListAsync(ct);

        List<NameCount> byAssignee = [];
        List<AuditLog> activity = [];
        List<DepartmentLoad> byDepartment = [];
        if (tier >= PermissionScope.Department)
        {
            var assigneeRows = await scope
                .GroupBy(t => t.Assignee != null ? t.Assignee.DisplayName : "Unassigned")
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToListAsync(ct);
            byAssignee = assigneeRows.Select(r => new NameCount(r.Name, r.Count)).ToList();

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

        string? departmentName = actor.DepartmentId is Guid deptId
            ? await db.Departments.Where(d => d.Id == deptId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;

        return new DashboardModel
        {
            Actor = actor,
            Tier = tier,
            Today = today,
            DepartmentName = departmentName,
            ScopeLabel = scopeLabel,
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
    /// its own tasks (tasks.edit at Own, or not at all) gets the personal dashboard, since its own work is what it acts
    /// on - which keeps the shipped Member role on the dashboard it always had.
    /// </summary>
    public static PermissionScope DashboardTier(Actor actor)
    {
        var tier = actor.ScopeOf(Permission.TasksView);
        if (tier == PermissionScope.Department && actor.ScopeOf(Permission.TasksEdit) <= PermissionScope.Own)
            return PermissionScope.Own;
        return tier;
    }

    private static IQueryable<TaskItem> Detailed(IQueryable<TaskItem> q) => q
        .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department).Include(t => t.Asset)
        .OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenByDescending(EnumOrder.ByTaskPriority).ThenByDescending(t => t.CreatedAt);
}

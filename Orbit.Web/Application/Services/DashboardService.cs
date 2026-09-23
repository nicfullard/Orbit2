using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Builds the role-tiered post-login dashboard (§6.9).</summary>
public sealed class DashboardService(ApplicationDbContext db, IActorProvider actors)
{
    public async Task<DashboardModel> BuildAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dueHorizon = today.AddDays(7);

        var open = db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);

        // Members see their own work; admins see their department (or the whole company).
        var scope = actor.Role switch
        {
            OrbitRole.SystemAdmin => open,
            OrbitRole.DepartmentAdmin => open.Where(t => t.DepartmentId == actor.DepartmentId),
            _ => open.Where(t => t.AssigneeId == actor.UserId)
        };
        var scopeLabel = actor.Role switch
        {
            OrbitRole.SystemAdmin => "Open tasks across the company",
            OrbitRole.DepartmentAdmin => "Open tasks in your department",
            _ => "My open tasks"
        };

        // Today's day plan (§6.12): same role scope, but over all tasks rather than open ones so "done today" counts.
        var allInScope = actor.Role switch
        {
            OrbitRole.SystemAdmin => db.Tasks.AsNoTracking(),
            OrbitRole.DepartmentAdmin => db.Tasks.AsNoTracking().Where(t => t.DepartmentId == actor.DepartmentId),
            _ => db.Tasks.AsNoTracking().Where(t => t.AssigneeId == actor.UserId)
        };
        var plannedToday = await allInScope.CountAsync(t => t.PlannedFor == today, ct);
        var plannedTodayDone = await allInScope.CountAsync(t => t.PlannedFor == today && t.Status == TaskItemStatus.Done, ct);

        var statusCounts = await scope.GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(TaskItemStatus s) => statusCounts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        var overdue = await scope.CountAsync(t => t.DueDate != null && t.DueDate < today, ct);

        var openTasks = await Detailed(scope).Take(12).ToListAsync(ct);
        var myOpen = actor.IsMember
            ? openTasks
            : await Detailed(open.Where(t => t.AssigneeId == actor.UserId)).Take(8).ToListAsync(ct);

        // Work nobody has picked up yet, which a Member may take (§6.5). Admins already see the whole department above.
        List<TaskItem> upForGrabs = [];
        if (actor.IsMember)
            upForGrabs = await Detailed(open.Where(t => t.DepartmentId == actor.DepartmentId && t.AssigneeId == null)).Take(8).ToListAsync(ct);

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
            var sprintScope = actor.Role switch
            {
                OrbitRole.SystemAdmin => all,
                OrbitRole.DepartmentAdmin => all.Where(t => t.DepartmentId == actor.DepartmentId),
                _ => all.Where(t => t.AssigneeId == actor.UserId)
            };
            sprintTasks = await sprintScope
                .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department)
                .OrderBy(t => t.Status).ThenByDescending(t => t.Priority).Take(20).ToListAsync(ct);
            if (actor.IsSystemAdmin)
            {
                var rows = await all.GroupBy(t => t.Department.Name)
                    .Select(g => new { Name = g.Key, Total = g.Count(), Done = g.Count(t => t.Status == TaskItemStatus.Done) })
                    .OrderBy(x => x.Name).ToListAsync(ct);
                sprintByDept = rows.Select(r => new DepartmentSprintProgress(r.Name, r.Total, r.Done)).ToList();
            }
        }

        // Projects at a glance
        var projects = db.Projects.AsNoTracking().Include(p => p.Department).Include(p => p.Owner)
            .Where(p => p.Status == ProjectStatus.Active);
        projects = actor.Role switch
        {
            OrbitRole.SystemAdmin => projects,
            OrbitRole.DepartmentAdmin => projects.Where(p => p.DepartmentId == actor.DepartmentId || p.Tasks.Any(t => t.DepartmentId == actor.DepartmentId)),
            _ => projects.Where(p => p.Tasks.Any(t => t.AssigneeId == actor.UserId))
        };
        var projectRows = await projects.OrderBy(p => p.Name).Take(12).Select(p => new
        {
            Project = p,
            Total = p.Tasks.Count(),
            Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
            Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
        }).ToListAsync(ct);

        var dueSoon = await scope.Where(t => t.DueDate != null && t.DueDate <= dueHorizon)
            .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department)
            .OrderBy(t => t.DueDate).Take(10).ToListAsync(ct);

        List<NameCount> byAssignee = [];
        List<AuditLog> activity = [];
        List<DepartmentLoad> byDepartment = [];
        if (!actor.IsMember)
        {
            var assigneeRows = await scope
                .GroupBy(t => t.Assignee != null ? t.Assignee.DisplayName : "Unassigned")
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToListAsync(ct);
            byAssignee = assigneeRows.Select(r => new NameCount(r.Name, r.Count)).ToList();

            var feed = db.AuditLogs.AsNoTracking().Where(a => a.EntityType == AuditEntity.Task);
            if (!actor.IsSystemAdmin) feed = feed.Where(a => a.DepartmentId == actor.DepartmentId);
            activity = await feed.OrderByDescending(a => a.Timestamp).Take(15).ToListAsync(ct);
        }
        if (actor.IsSystemAdmin)
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
            Today = today,
            DepartmentName = departmentName,
            ScopeLabel = scopeLabel,
            TodoCount = Count(TaskItemStatus.Todo),
            InProgressCount = Count(TaskItemStatus.InProgress),
            BlockedCount = Count(TaskItemStatus.Blocked),
            OverdueCount = overdue,
            PlannedTodayCount = plannedToday,
            PlannedTodayDone = plannedTodayDone,
            OpenTasks = openTasks,
            MyOpenTasks = myOpen,
            UpForGrabs = upForGrabs,
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

    private static IQueryable<TaskItem> Detailed(IQueryable<TaskItem> q) => q
        .Include(t => t.Project).Include(t => t.Assignee).Include(t => t.Department)
        .OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt);
}

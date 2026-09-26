using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Operational reports (§12). Aggregates are computed in SQL and returned as plain DTOs.</summary>
public sealed class ReportingService(ApplicationDbContext db, IActorProvider actors)
{
    private const string ClaudeName = "Claude";

    /// <summary>Tasks set to Done in the period, grouped by assignee.</summary>
    public async Task<IReadOnlyList<PersonCountRow>> ClosedByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var rows = await Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc)
            .GroupBy(t => t.AssigneeId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return rows.Select(r => new PersonCountRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Tasks created in the period, grouped by author. API-created tasks roll up under Claude.</summary>
    public async Task<IReadOnlyList<PersonCountRow>> CreatedByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var rows = await Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc)
            .GroupBy(t => t.CreatedById)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return rows.Select(r => new PersonCountRow(r.UserId, Name(r.UserId, names, "System (recurring)"), r.Count))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Average FirstRespondedAt - CreatedAt for tasks created in the period that have responded.</summary>
    public async Task<MeanTimeReport> MeanTimeToRespondAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var q = Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.FirstRespondedAt != null && t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc);
        var rows = await q.GroupBy(t => t.AssigneeId)
            .Select(g => new
            {
                UserId = g.Key,
                Count = g.Count(),
                AvgHours = g.Average(t => (t.FirstRespondedAt!.Value - t.CreatedAt).TotalHours)
            }).ToListAsync(ct);
        var overallCount = await q.CountAsync(ct);
        double? overall = overallCount == 0 ? null
            : await q.AverageAsync(t => (t.FirstRespondedAt!.Value - t.CreatedAt).TotalHours, ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new MeanTimeReport(
            rows.Select(r => new PersonAverageRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count, r.AvgHours))
                .OrderBy(r => r.AverageHours).ToList(),
            overallCount, overall);
    }

    /// <summary>Average CompletedAt - CreatedAt for tasks completed in the period.</summary>
    public async Task<MeanTimeReport> MeanTimeToResolveAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var q = Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc);
        var rows = await q.GroupBy(t => t.AssigneeId)
            .Select(g => new
            {
                UserId = g.Key,
                Count = g.Count(),
                AvgHours = g.Average(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours)
            }).ToListAsync(ct);
        var overallCount = await q.CountAsync(ct);
        double? overall = overallCount == 0 ? null
            : await q.AverageAsync(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours, ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new MeanTimeReport(
            rows.Select(r => new PersonAverageRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count, r.AvgHours))
                .OrderBy(r => r.AverageHours).ToList(),
            overallCount, overall);
    }

    /// <summary>
    /// Time logged in the period (by entry date) per person, with each task's estimate against all time logged on it
    /// to date. Scoped by the task's project and department, like time logging itself (§6.2.1). The people the report
    /// covers (<see cref="PeopleAsync"/>) are listed with zero when they logged nothing.
    /// </summary>
    public async Task<TimeByPersonReport> TimeByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var from = DateOnly.FromDateTime(f.FromUtc);
        var to = DateOnly.FromDateTime(f.ToUtc);
        var logged = await Apply(db.TimeEntries.AsNoTracking(), f, actor)
            .Where(e => e.Date >= from && e.Date < to)
            .GroupBy(e => new { e.UserId, e.TaskId })
            .Select(g => new LoggedTime(g.Key.UserId, g.Key.TaskId, g.Sum(e => e.DurationMinutes)))
            .ToListAsync(ct);
        var taskIds = logged.Select(l => l.TaskId).Distinct().ToList();
        var tasks = taskIds.Count == 0 ? new Dictionary<Guid, TaskTimeFacts>()
            : await Facts(db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id))).ToDictionaryAsync(t => t.Id, ct);
        var people = await PeopleAsync(f, actor, ct);
        var names = await NamesAsync(logged.Select(l => l.UserId).Concat(people).Select(id => (Guid?)id), ct);
        return TimeReportRules.TimeByPerson(logged, tasks, id => Name(id, names, "Unknown"), people);
    }

    /// <summary>
    /// The people Time by person lists even with nothing logged: active people (never the Claude user) whose own
    /// department is the one reported on - the project's when only a project is chosen - or everyone when neither is.
    /// </summary>
    private async Task<List<Guid>> PeopleAsync(ReportFilter f, Actor actor, CancellationToken ct)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (departmentId is null && f.ProjectId is Guid p)
            departmentId = await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => (Guid?)x.DepartmentId).FirstOrDefaultAsync(ct)
                ?? Guid.Empty; // an unknown project covers nobody
        var q = db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsSystemAccount);
        if (departmentId is Guid d) q = q.Where(u => u.DepartmentId == d);
        return await q.Select(u => u.Id).ToListAsync(ct);
    }

    /// <summary>Tasks set to Done in the period, grouped by assignee: the estimate against all time logged on them.</summary>
    public async Task<EstimateAccuracyReport> EstimateAccuracyAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var done = await Facts(Apply(db.Tasks.AsNoTracking(), f, actor)
                .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc))
            .ToListAsync(ct);
        var names = await NamesAsync(done.Select(t => t.AssigneeId), ct);
        return TimeReportRules.EstimateAccuracy(done, id => Name(id, names, "Unassigned"));
    }

    private static IQueryable<TaskTimeFacts> Facts(IQueryable<TaskItem> q) =>
        q.Select(t => new TaskTimeFacts(t.Id, t.Number, t.Title, t.Status, t.AssigneeId, t.EstimateMinutes,
            t.TimeEntries.Sum(e => (int?)e.DurationMinutes) ?? 0));

    private async Task<Actor> RequireReportsAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanViewReports(actor), "You don't have permission to view reports.");
        return actor;
    }

    /// <summary>The caller's own department is the only one they can report on unless reports.view is granted for all (§6.5).</summary>
    public static Guid? RestrictedDepartment(Actor actor) =>
        actor.CanAnywhere(Permission.ReportsView) ? null : actor.DepartmentId ?? Guid.Empty;

    private static IQueryable<TaskItem> Apply(IQueryable<TaskItem> q, ReportFilter f, Actor actor)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (f.ProjectId is Guid p) q = q.Where(t => t.ProjectId == p);
        if (departmentId is Guid d) q = q.Where(t => t.DepartmentId == d);
        return q;
    }

    /// <summary>Time entries take their project and department from their task.</summary>
    private static IQueryable<TimeEntry> Apply(IQueryable<TimeEntry> q, ReportFilter f, Actor actor)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (f.ProjectId is Guid p) q = q.Where(e => e.Task.ProjectId == p);
        if (departmentId is Guid d) q = q.Where(e => e.Task.DepartmentId == d);
        return q;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.IsSystemAccount ? ClaudeName : u.DisplayName, ct);
    }

    private static string Name(Guid? id, IReadOnlyDictionary<Guid, string> names, string fallbackForNull)
    {
        if (id is null) return fallbackForNull;
        if (id == WellKnownIds.ClaudeAgentUserId) return ClaudeName;
        return names.TryGetValue(id.Value, out var n) ? n : "Unknown";
    }
}

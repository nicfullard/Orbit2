using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Company-wide sprints. The list is open to every signed-in user; the tasks inside a sprint follow tasks.view; create/start/complete needs sprints.manage.</summary>
public sealed class SprintService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    public async Task<IReadOnlyList<SprintListItem>> ListAsync(CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var sprints = await db.Sprints.AsNoTracking()
            .OrderBy(s => s.Status == SprintStatus.Active ? 0 : s.Status == SprintStatus.Planned ? 1 : 2)
            .ThenByDescending(s => s.StartDate)
            .ToListAsync(ct);

        var counts = await db.Tasks.AsNoTracking().Where(t => t.SprintId != null)
            .GroupBy(t => new { t.SprintId, DepartmentName = t.Department.Name })
            .Select(g => new
            {
                g.Key.SprintId,
                g.Key.DepartmentName,
                Total = g.Count(),
                Open = g.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
            })
            .ToListAsync(ct);

        return sprints.Select(s =>
        {
            var rows = counts.Where(c => c.SprintId == s.Id).ToList();
            return new SprintListItem(s, rows.Sum(r => r.Total), rows.Sum(r => r.Open),
                rows.OrderBy(r => r.DepartmentName).Select(r => new NameCount(r.DepartmentName, r.Total)).ToList());
        }).ToList();
    }

    /// <summary>Sprints tasks can still be planned into.</summary>
    public async Task<IReadOnlyList<Sprint>> ListOpenAsync(CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.Sprints.AsNoTracking().Where(s => s.Status != SprintStatus.Completed)
            .OrderBy(s => s.Status == SprintStatus.Active ? 0 : 1).ThenBy(s => s.StartDate).ToListAsync(ct);
    }

    /// <summary>The sprint with the tasks in it that the caller may see (tasks.view, §6.5).</summary>
    public async Task<Sprint> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Sprint not found.");
        sprint.Tasks = await Scoping.Tasks(db.Tasks.AsNoTracking().Where(t => t.SprintId == id), actor)
            .Include(t => t.Department).Include(t => t.Project).Include(t => t.Assignee).Include(t => t.ParentTask)
            .ToListAsync(ct);
        return sprint;
    }

    public async Task<Sprint?> GetActiveAsync(CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Status == SprintStatus.Active, ct);
    }

    public async Task<SprintBoard> GetBoardAsync(Guid id, CancellationToken ct = default)
    {
        var sprint = await GetAsync(id, ct);
        var tasks = sprint.Tasks
            .OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Title)
            .ToList();
        SprintBoardColumn Column(string title, TaskItemStatus status, Func<TaskItem, bool> pick) =>
            new(title, status, tasks.Where(pick).ToList());
        var columns = new List<SprintBoardColumn>
        {
            Column("To do", TaskItemStatus.Todo, t => t.Status == TaskItemStatus.Todo),
            Column("In progress", TaskItemStatus.InProgress, t => t.Status == TaskItemStatus.InProgress),
            Column("Waiting", TaskItemStatus.Waiting, t => t.Status == TaskItemStatus.Waiting),
            Column("Blocked", TaskItemStatus.Blocked, t => t.Status == TaskItemStatus.Blocked),
            Column("Done", TaskItemStatus.Done, t => t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled)
        };
        return new SprintBoard(sprint, columns);
    }

    public async Task<Sprint> CreateAsync(SprintInput input, CancellationToken ct = default)
    {
        var actor = await RequireSprintManagerAsync(ct);
        Validate(input);
        var sprint = new Sprint
        {
            Name = input.Name.Trim(),
            Goal = Clean(input.Goal),
            StartDate = input.StartDate,
            EndDate = input.EndDate,
            Status = SprintStatus.Planned,
            CreatedAt = DateTime.UtcNow
        };
        db.Sprints.Add(sprint);
        audit.Add(actor, AuditEntity.Sprint, sprint.Id, AuditAction.Created, null, sprint.Name,
            new { sprint.Name, sprint.StartDate, sprint.EndDate });
        await db.SaveChangesAsync(ct);
        return sprint;
    }

    public async Task<Sprint> UpdateAsync(Guid id, SprintInput input, CancellationToken ct = default)
    {
        var actor = await RequireSprintManagerAsync(ct);
        Validate(input);
        var sprint = await db.Sprints.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Sprint not found.");
        var changes = new ChangeSet()
            .TrackText("name", sprint.Name, input.Name)
            .TrackText("goal", sprint.Goal, input.Goal)
            .Track("startDate", sprint.StartDate, input.StartDate)
            .Track("endDate", sprint.EndDate, input.EndDate);
        if (!changes.HasChanges) return sprint;
        sprint.Name = input.Name.Trim();
        sprint.Goal = Clean(input.Goal);
        sprint.StartDate = input.StartDate;
        sprint.EndDate = input.EndDate;
        audit.Add(actor, AuditEntity.Sprint, sprint.Id, AuditAction.Updated, null, sprint.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return sprint;
    }

    /// <summary>
    /// Planned -> Active. If another sprint is active it is completed and its still-open tasks roll
    /// forward onto the new sprint, all in one transaction.
    /// </summary>
    public async Task<Sprint> StartAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireSprintManagerAsync(ct);
        var sprint = await db.Sprints.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Sprint not found.");
        if (sprint.Status != SprintStatus.Planned)
            throw new ValidationException("Only a planned sprint can be started.");

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var current = await db.Sprints.FirstOrDefaultAsync(s => s.Status == SprintStatus.Active, ct);
        if (current is not null)
        {
            var open = await db.Tasks.Where(t => t.SprintId == current.Id
                && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled).ToListAsync(ct);
            foreach (var task in open)
            {
                task.SprintId = sprint.Id;
                task.UpdatedAt = now;
            }
            current.Status = SprintStatus.Completed;
            current.CompletedAt = now;
            audit.Add(actor, AuditEntity.Sprint, current.Id, AuditAction.Completed, null, current.Name,
                new { rolledForwardTo = sprint.Id, rolledForwardTaskIds = open.Select(t => t.Id).ToArray() });
            // Save before activating the new sprint so the single-active-sprint index is never violated mid-batch.
            await db.SaveChangesAsync(ct);
        }

        sprint.Status = SprintStatus.Active;
        sprint.StartedAt = now;
        audit.Add(actor, AuditEntity.Sprint, sprint.Id, AuditAction.Started, null, sprint.Name,
            new { previousSprintId = current?.Id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sprint;
    }

    /// <summary>Active -> Completed without a successor: still-open tasks return to the backlog.</summary>
    public async Task<Sprint> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireSprintManagerAsync(ct);
        var sprint = await db.Sprints.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Sprint not found.");
        if (sprint.Status != SprintStatus.Active)
            throw new ValidationException("Only the active sprint can be completed.");

        var now = DateTime.UtcNow;
        var open = await db.Tasks.Where(t => t.SprintId == sprint.Id
            && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled).ToListAsync(ct);
        foreach (var task in open)
        {
            task.SprintId = null;
            task.UpdatedAt = now;
        }
        sprint.Status = SprintStatus.Completed;
        sprint.CompletedAt = now;
        audit.Add(actor, AuditEntity.Sprint, sprint.Id, AuditAction.Completed, null, sprint.Name,
            new { returnedToBacklogTaskIds = open.Select(t => t.Id).ToArray() });
        await db.SaveChangesAsync(ct);
        return sprint;
    }

    private async Task<Actor> RequireSprintManagerAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageSprints(actor), "You don't have permission to manage sprints.");
        return actor;
    }

    private static void Validate(SprintInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ValidationException("Name is required.");
        if (input.EndDate < input.StartDate) throw new ValidationException("End date must be on or after the start date.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

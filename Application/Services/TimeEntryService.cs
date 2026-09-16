using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

public sealed class TimeEntryService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    public async Task<IReadOnlyList<TimeEntry>> ListForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        return await db.TimeEntries.AsNoTracking().Include(e => e.User)
            .Where(e => e.TaskId == taskId).OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task<TimeEntry> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var entry = await db.TimeEntries.AsNoTracking().Include(e => e.User).Include(e => e.Task)
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Time entry not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, entry.Task), "This task belongs to another department.");
        return entry;
    }

    public async Task<MyTimeSummary> MyTimeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (to < from) (from, to) = (to, from);
        var entries = await db.TimeEntries.AsNoTracking()
            .Include(e => e.Task).ThenInclude(t => t.Project)
            .Where(e => e.UserId == actor.UserId && e.Date >= from && e.Date <= to)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
        return new MyTimeSummary(entries, entries.Sum(e => e.DurationMinutes), from, to);
    }

    public async Task<TimeEntry> AddAsync(TimeEntryInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == input.TaskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");

        var userId = input.UserId ?? actor.UserId ?? throw new ValidationException("A user is required.");
        AccessPolicy.Require(AccessPolicy.CanLogTimeFor(actor, task, userId),
            "You can only log time on tasks assigned to you. Department Admins can log time for anyone in their department.");
        if (userId != actor.UserId) await ValidateTargetUserAsync(userId, task.DepartmentId, actor, ct);
        Validate(input);

        var entry = new TimeEntry
        {
            TaskId = task.Id,
            UserId = userId,
            Date = input.Date,
            DurationMinutes = input.DurationMinutes,
            Note = Clean(input.Note),
            CreatedAt = DateTime.UtcNow
        };
        db.TimeEntries.Add(entry);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.TimeLogged, task.DepartmentId, task.Title,
            new { timeEntryId = entry.Id, entry.UserId, entry.Date, entry.DurationMinutes });
        await db.SaveChangesAsync(ct);
        await db.Entry(entry).Reference(e => e.User).LoadAsync(ct);
        return entry;
    }

    public async Task<TimeEntry> UpdateAsync(Guid id, TimeEntryInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var entry = await db.TimeEntries.Include(e => e.Task).Include(e => e.User)
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Time entry not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, entry.Task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTimeEntry(actor, entry, entry.Task), "You can only edit your own time entries.");
        Validate(input);

        var changes = new ChangeSet()
            .Track("date", entry.Date, input.Date)
            .Track("durationMinutes", entry.DurationMinutes, input.DurationMinutes)
            .TrackText("note", entry.Note, input.Note);
        if (!changes.HasChanges) return entry;

        entry.Date = input.Date;
        entry.DurationMinutes = input.DurationMinutes;
        entry.Note = Clean(input.Note);
        audit.Add(actor, AuditEntity.Task, entry.TaskId, AuditAction.TimeUpdated, entry.Task.DepartmentId, entry.Task.Title,
            new { timeEntryId = entry.Id, changes = changes.Changes });
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<Guid> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var entry = await db.TimeEntries.Include(e => e.Task).FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Time entry not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, entry.Task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTimeEntry(actor, entry, entry.Task), "You can only delete your own time entries.");

        db.TimeEntries.Remove(entry);
        audit.Add(actor, AuditEntity.Task, entry.TaskId, AuditAction.TimeDeleted, entry.Task.DepartmentId, entry.Task.Title,
            new { timeEntryId = entry.Id, entry.Date, entry.DurationMinutes });
        await db.SaveChangesAsync(ct);
        return entry.TaskId;
    }

    private static void Validate(TimeEntryInput input)
    {
        if (input.DurationMinutes <= 0) throw new ValidationException("Duration must be at least one minute.");
        if (input.DurationMinutes > 24 * 60) throw new ValidationException("A single entry can't exceed 24 hours.");
        if (input.Date > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)) throw new ValidationException("The date can't be in the future.");
    }

    private async Task ValidateTargetUserAsync(Guid userId, Guid departmentId, Actor actor, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");
        if (!user.IsActive || user.IsSystemAccount) throw new ValidationException("Time can only be logged for active users.");
        if (!actor.IsSystemAdmin && user.DepartmentId != departmentId)
            throw new ValidationException("You can only log time for users in your own department.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

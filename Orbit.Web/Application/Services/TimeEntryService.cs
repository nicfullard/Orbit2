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

    /// <summary>
    /// Whether the caller should confirm before setting the task Done: it's theirs and they haven't logged time on it (§6.10).
    /// Asked by the web UI's status controls when Done is picked, so the answer is current.
    /// </summary>
    public async Task<bool> AskBeforeDoneWithoutTimeAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        if (actor.UserId is not Guid me || !task.IsAssignedTo(me)) return false;
        var logged = await db.TimeEntries.AnyAsync(e => e.TaskId == taskId && e.UserId == me, ct);
        var clockRunning = await db.RunningClocks.AnyAsync(c => c.TaskId == taskId && c.UserId == me, ct);
        return TimeRules.AskBeforeDoneWithoutTime(actor, task, logged, clockRunning);
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
        // A retried log_time with the same key gets the entry it already logged (§7.1), not the hours twice.
        var idempotencyKey = Clean(input.IdempotencyKey);
        if (idempotencyKey?.Length > MaxIdempotencyKeyLength)
            throw new ValidationException($"The idempotency key can't exceed {MaxIdempotencyKeyLength} characters.");
        if (idempotencyKey is not null && await FindByKeyAsync(idempotencyKey, ct) is Guid already)
            return await GetAsync(already, ct);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == input.TaskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");

        var userId = input.UserId ?? actor.UserId ?? throw new ValidationException("A user is required.");
        AccessPolicy.Require(AccessPolicy.CanLogTimeFor(actor, task, userId),
            "You can only log time on tasks assigned to you, unless your role may log time for the whole department.");
        if (userId != actor.UserId) await ValidateTargetUserAsync(userId, task.DepartmentId, actor, ct);
        Validate(input);

        var entry = new TimeEntry
        {
            TaskId = task.Id,
            UserId = userId,
            Date = input.Date,
            DurationMinutes = input.DurationMinutes,
            Note = Clean(input.Note),
            CreatedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey
        };
        db.TimeEntries.Add(entry);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.TimeLogged, task.DepartmentId, task.Title,
            new { timeEntryId = entry.Id, entry.UserId, entry.Date, entry.DurationMinutes });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // A concurrent call with the same key saved first (the unique index refused this one): return its entry.
            db.ChangeTracker.Clear();
            if (await FindByKeyAsync(idempotencyKey, ct) is Guid raced) return await GetAsync(raced, ct);
            throw;
        }
        await db.Entry(entry).Reference(e => e.User).LoadAsync(ct);
        return entry;
    }

    private Task<Guid?> FindByKeyAsync(string key, CancellationToken ct) =>
        db.TimeEntries.AsNoTracking().Where(e => e.IdempotencyKey == key).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);

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

    // --- Start / Stop clock (§6.10) ---------------------------------------------------------------
    // A user has at most one clock per task, and the clocks on different tasks run independently: each task page runs
    // its own (§13 item 64). The page checks in every minute (HeartbeatAsync). A clock whose page stops checking in is
    // stale and is stopped at its last heartbeat: by its page if it wakes up, by the next task page the user opens, or
    // by ClockSweepJob within a minute or so.

    /// <summary>The caller's running clock on the task, or null.</summary>
    public async Task<RunningClock?> GetRunningClockAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) return null;
        return await db.RunningClocks.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == me && c.TaskId == taskId, ct);
    }

    /// <summary>
    /// Starts the caller's clock on a task; clocks running on other tasks are left alone. A clock already running on
    /// this task (it is open in another tab) is left as it is, unless it is stale: then it is stopped at its last
    /// heartbeat and a new one started.
    /// </summary>
    public async Task<ClockStartResult> StartClockAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var me = actor.UserId ?? throw new ForbiddenException("Only signed-in users can run the clock.");
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanLogTimeFor(actor, task, me), "You can only run the clock on tasks assigned to you.");

        var now = DateTime.UtcNow;
        ClockStopResult? previous = null;
        if (await FindClockAsync(me, taskId, ct) is { } existing)
        {
            if (!TimeRules.IsClockStale(existing.LastSeenAt, now)) return new ClockStartResult(existing, null, AlreadyRunning: true);
            previous = await SaveStopAsync(actor, existing, now, ct);
        }

        var clock = new RunningClock { UserId = me, TaskId = task.Id, StartedAt = now, LastSeenAt = now };
        db.RunningClocks.Add(clock);
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.ClockStarted, task.DepartmentId, task.Title, new { clock.StartedAt });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Started at the same moment from another tab of this task: the unique index refused this one.
            throw new ValidationException("The clock is already running on this task. Reload the page to see it.");
        }
        return new ClockStartResult(clock, previous, AlreadyRunning: false);
    }

    /// <summary>
    /// Stops the caller's clock on the task and logs the elapsed time as a time entry. Returns null when no clock was
    /// running on it. Under half a minute is discarded rather than logged; anything over 24 hours is capped at 24 hours.
    /// </summary>
    public async Task<ClockStopResult?> StopClockAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) return null;
        var clock = await FindClockAsync(me, taskId, ct);
        return clock is null ? null : await SaveStopAsync(actor, clock, DateTime.UtcNow, ct);
    }

    /// <summary>
    /// A task page's heartbeat: keeps the caller's clock on the task alive. A clock found stale (its page was asleep or
    /// frozen for longer than <see cref="TimeRules.ClockStaleAfter"/>) is stopped at its last heartbeat instead, so the
    /// time nobody was watching isn't logged. Not running tells the page to reload.
    /// </summary>
    public async Task<ClockHeartbeatResult> HeartbeatAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) return new ClockHeartbeatResult(false, null);
        var clock = await FindClockAsync(me, taskId, ct);
        if (clock is null) return new ClockHeartbeatResult(false, null);

        var now = DateTime.UtcNow;
        if (TimeRules.IsClockStale(clock.LastSeenAt, now))
            return new ClockHeartbeatResult(false, await SaveStopAsync(actor, clock, now, ct));
        clock.LastSeenAt = now; // not audited: a row a minute per running clock would bury the task's history
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Stopped meanwhile (the Stop button in another tab of this task, say).
            db.ChangeTracker.Clear();
            return new ClockHeartbeatResult(false, null);
        }
        return new ClockHeartbeatResult(true, null);
    }

    /// <summary>Stops the caller's stale clocks, each at its last heartbeat, so the task page can say so.</summary>
    public async Task<IReadOnlyList<ClockStopResult>> StopStaleClocksAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        return actor.UserId is Guid me ? await StopStaleCoreAsync(actor, me, ct) : [];
    }

    /// <summary>Stops every user's stale clocks, each at its last heartbeat, as the system. Run by ClockSweepJob.</summary>
    public async Task<int> StopAllStaleClocksAsync(CancellationToken ct = default) =>
        (await StopStaleCoreAsync(Actor.System, userId: null, ct)).Count;

    private async Task<List<ClockStopResult>> StopStaleCoreAsync(Actor actor, Guid? userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - TimeRules.ClockStaleAfter;
        var stale = await db.RunningClocks.Include(c => c.Task)
            .Where(c => c.LastSeenAt < cutoff && (userId == null || c.UserId == userId))
            .ToListAsync(ct);
        var stopped = new List<ClockStopResult>();
        foreach (var clock in stale)
        {
            if (await SaveStopAsync(actor, clock, now, ct) is { } result) stopped.Add(result);
        }
        return stopped;
    }

    private Task<RunningClock?> FindClockAsync(Guid userId, Guid taskId, CancellationToken ct) =>
        db.RunningClocks.Include(c => c.Task).FirstOrDefaultAsync(c => c.UserId == userId && c.TaskId == taskId, ct);

    /// <summary>
    /// Stops a tracked clock, logs its time for its user and saves. Null when a concurrent request stopped it first
    /// (the Stop button and the page-leave beacon both firing, or the sweep).
    /// </summary>
    private async Task<ClockStopResult?> SaveStopAsync(Actor actor, RunningClock clock, DateTime now, CancellationToken ct)
    {
        var startedAt = DateTime.SpecifyKind(clock.StartedAt, DateTimeKind.Utc);
        var lastSeenAt = DateTime.SpecifyKind(clock.LastSeenAt, DateTimeKind.Utc);
        var stale = TimeRules.IsClockStale(lastSeenAt, now);
        var stoppedAt = TimeRules.ClockStoppedAt(lastSeenAt, now);
        var minutes = TimeRules.ClockMinutes(startedAt, stoppedAt);
        var task = clock.Task;

        db.RunningClocks.Remove(clock);
        TimeEntry? entry = null;
        if (minutes >= 1)
        {
            entry = new TimeEntry
            {
                TaskId = task.Id,
                UserId = clock.UserId,
                Date = DateOnly.FromDateTime(startedAt),
                DurationMinutes = minutes,
                Note = $"Clock {startedAt.ToLocalTime():HH:mm}-{stoppedAt.ToLocalTime():HH:mm}",
                CreatedAt = now
            };
            db.TimeEntries.Add(entry);
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.TimeLogged, task.DepartmentId, task.Title,
                new { timeEntryId = entry.Id, entry.UserId, entry.Date, entry.DurationMinutes, clock = true });
        }
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.ClockStopped, task.DepartmentId, task.Title,
            new { startedAt, stoppedAt, minutes, timeEntryId = entry?.Id, stale });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
        return new ClockStopResult(task, entry, minutes, stale ? stoppedAt : null);
    }

    private const int MaxNoteLength = 1000;
    private const int MaxIdempotencyKeyLength = 200;

    private static void Validate(TimeEntryInput input)
    {
        if (input.DurationMinutes <= 0) throw new ValidationException("Duration must be at least one minute.");
        if (input.DurationMinutes > 24 * 60) throw new ValidationException("A single entry can't exceed 24 hours.");
        if (input.Date > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)) throw new ValidationException("The date can't be in the future.");
        if (Clean(input.Note)?.Length > MaxNoteLength) throw new ValidationException($"The note can't exceed {MaxNoteLength} characters.");
    }

    private async Task ValidateTargetUserAsync(Guid userId, Guid departmentId, Actor actor, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");
        if (!user.IsActive || user.IsSystemAccount) throw new ValidationException("Time can only be logged for active users.");
        if (!actor.CanAnywhere(Permission.TimeLog) && user.DepartmentId != departmentId)
            throw new ValidationException("You can only log time for users in your own department.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

using Microsoft.EntityFrameworkCore;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Application.Nagios;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Checks the Nagios instances and acts on what they report (spec §6.21): reads each due instance through its agent, opens and
/// resolves incidents, raises a task for a problem that has outlasted its threshold, and notes on the task when Nagios reports it
/// well again. Runs in the Nagios job as <see cref="Actor.System"/> - there is no signed-in person, so it never asks
/// <see cref="IActorProvider"/> - and builds the task itself, as the recurring-task job does.
/// </summary>
public sealed class NagiosMonitor(
    ApplicationDbContext db, AuditService audit, NumberingService numbering, NotificationService notifications,
    AgentNagiosDispatcher dispatcher, NagiosPasswordProtector passwords, ILogger<NagiosMonitor> logger)
{
    private const int MaxErrorLength = 1000;

    /// <summary>Checks every enabled instance whose interval has passed. Returns how many were read and applied.</summary>
    public async Task<int> CheckDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var candidates = await db.NagiosInstances.AsNoTracking().Where(n => n.Enabled)
            .Select(n => new { n.Id, n.LastAttemptAt, n.CheckIntervalMinutes }).ToListAsync(ct);
        var applied = 0;
        foreach (var candidate in candidates.Where(c => NagiosIncidentRules.IsDue(true, c.LastAttemptAt, c.CheckIntervalMinutes, now)))
        {
            ct.ThrowIfCancellationRequested();
            if (await CheckAsync(candidate.Id, ct)) applied++;
        }
        return applied;
    }

    /// <summary>
    /// Checks one enabled instance now, whatever its interval. True when Nagios was read and the reading applied. A failure - no
    /// agent, a wrong password, Nagios not answering - changes no incident: it is recorded on the instance and the next check tries again.
    /// </summary>
    public async Task<bool> CheckAsync(Guid instanceId, CancellationToken ct = default)
    {
        try
        {
            var instance = await db.NagiosInstances.AsNoTracking().Include(n => n.Agent).FirstOrDefaultAsync(n => n.Id == instanceId, ct);
            if (instance is null || !instance.Enabled) return false;

            if (passwords.Unprotect(instance.PasswordProtected) is not { } password)
                return await FailAsync(instanceId, "The stored password can't be read. Enter it again under Admin > Nagios.", attempted: true, ct);

            var reading = await dispatcher.ReadAsync(instance.AgentId, instance.Agent?.Name, instance.BaseUrl, instance.Username, password,
                instance.ValidateCertificate, NagiosQueries.Check, ct);
            if (reading.Snapshot is null)
                // No agent to ask is not an answer from Nagios: leave the instance due, so it is tried again at the next tick.
                return await FailAsync(instanceId, reading.Error ?? "Nagios could not be read.", attempted: !reading.NoAgent, ct);

            var raised = await ApplyAsync(instanceId, reading.Snapshot, ct);
            foreach (var (task, people) in raised)
            {
                foreach (var person in people)
                {
                    try { await notifications.TaskAssignedAsync(task, person, Actor.System, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Couldn't notify {User} about Nagios task {Task}.", person.Id, task.Number);
                    }
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checking Nagios instance {Instance} failed.", instanceId);
            db.ChangeTracker.Clear();
            await FailAsync(instanceId, $"Orbit could not record this check: {ex.GetBaseException().Message}", attempted: true, CancellationToken.None);
            return false;
        }
        finally
        {
            // One DbContext serves every instance in a job run: nothing of this one may leak into the next.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Applies one reading in one transaction. The instance's row is locked first, so two checks of the same instance can't both
    /// act on it, and a reading that is not newer than the last one applied is dropped. Returns the tasks created, with who to tell.
    /// </summary>
    private async Task<List<(TaskItem Task, List<ApplicationUser> People)>> ApplyAsync(Guid instanceId, NagiosSnapshot snapshot, CancellationToken ct)
    {
        var created = new List<(TaskItem, List<ApplicationUser>)>();
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The row lock: another check of this instance waits here until this transaction ends, then sees its writes.
        DateTime? attemptedAt = now;
        if (await db.NagiosInstances.Where(n => n.Id == instanceId).ExecuteUpdateAsync(s => s.SetProperty(n => n.LastAttemptAt, attemptedAt), ct) == 0)
            return created;
        var instance = await db.NagiosInstances.Include(n => n.Department).Include(n => n.Assignees).ThenInclude(a => a.User)
            .FirstAsync(n => n.Id == instanceId, ct);
        if (!NagiosIncidentRules.IsNewer(instance.LastQueryTime, snapshot.QueryTime))
        {
            await tx.CommitAsync(ct);
            return created;
        }

        var open = await db.NagiosIncidents.Include(i => i.Task)
            .Where(i => i.NagiosInstanceId == instanceId && i.ResolvedAt == null).ToListAsync(ct);
        var openTasks = await OpenTasksAsync(db, instanceId, ct);

        var plan = NagiosIncidentRules.Plan(NagiosRuleSettings.From(instance), snapshot,
            open.Select(i => new NagiosOpenIncident(i.Id, i.HostName, i.ServiceDescription, i.ClockFrom, i.LastOkAt, i.TaskId is not null)).ToList(),
            openTasks.Keys.ToHashSet());

        // Incidents that ended. Saved before anything opens: an object that failed again gets a new incident at this same check,
        // and the unique index allows only one open incident per object.
        foreach (var resolve in plan.Resolves)
        {
            var incident = open.First(i => i.Id == resolve.IncidentId);
            incident.ResolvedAt = now;
            incident.Resolution = resolve.Resolution;
            if (incident.Task is not { } task) continue;
            AddNote(task, resolve.Resolution == NagiosResolution.Vanished
                ? NagiosText.VanishedNote(instance.Name, incident.HostName, incident.ServiceDescription)
                : resolve.FailedAgain
                    ? NagiosText.RecoveredAndFailedAgainNote(instance.Name, incident.HostName, incident.ServiceDescription)
                    : NagiosText.RecoveredNote(instance.Name, incident.HostName, incident.ServiceDescription, snapshot.QueryTime, task.IsOpen), now);
        }
        if (plan.Resolves.Count > 0) await db.SaveChangesAsync(ct);

        // An archived department takes no new tasks (§6.6). The problems stay watched and are raised once the instance files elsewhere.
        var canRaise = !instance.Department.IsArchived;
        var people = instance.Assignees.Select(a => a.User).Where(u => u is { IsActive: true, IsSystemAccount: false }).OrderBy(u => u.DisplayName).ToList();
        NagiosCgiRules.TryCgiBase(instance.BaseUrl, out var cgiBase, out _);
        var held = plan.HeldBack;

        foreach (var decision in plan.Decisions)
        {
            var problem = decision.Problem;
            var incident = decision.IncidentId is Guid id ? open.First(i => i.Id == id) : null;
            if (incident is null && decision.OpensIncident)
            {
                incident = new NagiosIncident
                {
                    NagiosInstanceId = instanceId, HostName = problem.Host, ServiceDescription = problem.Service,
                    ProblemSince = problem.Since, LastOkAt = problem.LastOkAt, OpenedAt = now
                };
                db.NagiosIncidents.Add(incident);
            }
            if (incident is null) continue;
            incident.State = problem.State;
            incident.Output = problem.Output;
            incident.ClockFrom = decision.ClockFrom;
            incident.LastSeenAt = now;

            if (decision.Verdict == NagiosVerdict.Join)
            {
                var taskId = openTasks[(problem.Host, problem.Service)];
                var task = await db.Tasks.FirstAsync(t => t.Id == taskId, ct);
                incident.TaskId = task.Id;
                AddNote(task, NagiosText.DownAgainNote(instance.Name, problem), now);
            }
            else if (decision.Verdict == NagiosVerdict.Raise && !canRaise)
            {
                held++;
            }
            else if (decision.Verdict == NagiosVerdict.Raise)
            {
                var link = cgiBase is null ? null : NagiosCgiRules.ExtInfoUrl(cgiBase, problem.Host, problem.Service);
                var task = new TaskItem
                {
                    Number = await numbering.NextAsync(NumberingService.TaskPrefix, now, ct),
                    Title = NagiosText.TaskTitle(problem),
                    Description = NagiosText.TaskDescription(instance.Name, problem, link),
                    DepartmentId = instance.DepartmentId,
                    Priority = instance.TaskPriority,
                    Assignments = [],
                    Status = TaskItemStatus.Todo,
                    Source = TaskSource.Nagios,
                    CreatedById = null,
                    SprintId = null,
                    // One incident, one task: the unique index on the key makes that true in the database as well.
                    IdempotencyKey = $"nagios:{incident.Id}",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                // The instance assigned them, not a person: AssignedById stays null.
                foreach (var person in people)
                    task.Assignments.Add(new TaskAssignment { TaskId = task.Id, UserId = person.Id, AssignedAt = now });
                db.Tasks.Add(task);
                incident.TaskId = task.Id;
                audit.Add(Actor.System, AuditEntity.Task, task.Id, AuditAction.Generated, instance.DepartmentId, task.Title, new
                {
                    nagios = new { instanceId, instance = instance.Name, host = problem.Host, service = problem.IsService ? problem.Service : null, state = problem.State, since = problem.Since },
                    task.Priority,
                    assigneeIds = people.Select(u => u.Id).ToList()
                });
                created.Add((task, people));
            }
        }

        instance.LastSucceededAt = now;
        instance.LastQueryTime = snapshot.QueryTime;
        instance.HeldBack = held;
        instance.LastError = canRaise ? null : Clip($"The department \"{instance.Department.Name}\" is archived, so no tasks are raised. Choose another department for this instance.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (created.Count > 0 || plan.Resolves.Count > 0)
            logger.LogInformation("Nagios instance {Instance}: {Raised} task(s) raised, {Resolved} incident(s) ended, {Held} held back.",
                instance.Name, created.Count, plan.Resolves.Count, held);
        return created;
    }

    /// <summary>
    /// For each host or service of the instance with a task that is not closed - through any of its incidents, ended or not - that
    /// task (the latest, should there be several). A new incident of the object joins it rather than raising another.
    /// </summary>
    public static async Task<Dictionary<(string Host, string Service), Guid>> OpenTasksAsync(ApplicationDbContext db, Guid instanceId, CancellationToken ct) =>
        (await db.NagiosIncidents.AsNoTracking()
            .Where(i => i.NagiosInstanceId == instanceId && i.TaskId != null
                && i.Task!.Status != TaskItemStatus.Done && i.Task.Status != TaskItemStatus.Cancelled)
            .OrderBy(i => i.OpenedAt)
            .Select(i => new { i.HostName, i.ServiceDescription, TaskId = i.TaskId!.Value }).ToListAsync(ct))
        .GroupBy(i => (i.HostName, i.ServiceDescription)).ToDictionary(g => g.Key, g => g.Last().TaskId);

    /// <summary>A note on the task, written by nobody: a comment with no author, audited like any other.</summary>
    private void AddNote(TaskItem task, string body, DateTime now)
    {
        var comment = new Comment { TaskId = task.Id, AuthorId = null, Body = body, CreatedAt = now };
        db.Comments.Add(comment);
        task.UpdatedAt = now;
        audit.Add(Actor.System, AuditEntity.Task, task.Id, AuditAction.CommentAdded, task.DepartmentId, task.Title,
            new { commentId = comment.Id, body = body.Length > 500 ? body[..500] + "..." : body });
    }

    /// <summary>Records why a check did nothing. Always false, so a caller can return it.</summary>
    private async Task<bool> FailAsync(Guid instanceId, string error, bool attempted, CancellationToken ct)
    {
        DateTime? now = DateTime.UtcNow;
        var message = Clip(error);
        try
        {
            var instance = db.NagiosInstances.Where(n => n.Id == instanceId);
            if (attempted)
                await instance.ExecuteUpdateAsync(s => s.SetProperty(n => n.LastError, message).SetProperty(n => n.LastAttemptAt, now), ct);
            else
                await instance.ExecuteUpdateAsync(s => s.SetProperty(n => n.LastError, message), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Couldn't record the failed check of Nagios instance {Instance}: {Error}", instanceId, error);
        }
        return false;
    }

    private static string Clip(string text) => text.Length <= MaxErrorLength ? text : text[..(MaxErrorLength - 3)] + "...";
}

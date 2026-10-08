using Microsoft.EntityFrameworkCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Application.Nagios;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Jobs;
using Quartz;

namespace Orbit.Application.Services;

/// <summary>
/// The Nagios instances Orbit watches (spec §6.21, Admin &gt; Nagios): their address and sign-in, what raises a task and where it
/// goes. Everything here needs nagios.manage; the checking itself is <see cref="NagiosMonitor"/>'s. The password is encrypted with
/// the Data Protection key ring and is write-only from the UI's point of view; the audit log records that it changed, never to what.
/// </summary>
public sealed class NagiosService(
    ApplicationDbContext db, IActorProvider actors, AuditService audit, NagiosPasswordProtector passwords,
    AgentConnectionRegistry registry, AgentNagiosDispatcher dispatcher, ISchedulerFactory schedulers)
{
    private const int RecentIncidents = 50;

    public async Task<IReadOnlyList<NagiosInstanceListItem>> ListAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var instances = await db.NagiosInstances.AsNoTracking().Include(n => n.Agent).Include(n => n.Department)
            .OrderBy(n => n.Name).ToListAsync(ct);
        var counts = await db.NagiosIncidents.Where(i => i.ResolvedAt == null).GroupBy(i => i.NagiosInstanceId)
            .Select(g => new { g.Key, Open = g.Count(), Raised = g.Count(i => i.TaskId != null) }).ToDictionaryAsync(g => g.Key, ct);
        return instances.Select(n =>
        {
            var (online, queries) = AgentState(n.AgentId);
            counts.TryGetValue(n.Id, out var c);
            return new NagiosInstanceListItem(n, online, queries, c?.Open ?? 0, c?.Raised ?? 0);
        }).ToList();
    }

    public async Task<NagiosInstanceView> GetAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var instance = await db.NagiosInstances.AsNoTracking().Include(n => n.Agent).Include(n => n.Department).Include(n => n.Assignees)
            .FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new NotFoundException("Nagios instance not found.");
        return new NagiosInstanceView(instance, !string.IsNullOrEmpty(instance.PasswordProtected));
    }

    /// <summary>An instance with its open incidents - raised ones first - and the ones that ended most recently.</summary>
    public async Task<NagiosInstanceDetails> DetailsAsync(Guid id, CancellationToken ct = default)
    {
        var view = await GetAsync(id, ct);
        var open = await db.NagiosIncidents.AsNoTracking().Include(i => i.Task)
            .Where(i => i.NagiosInstanceId == id && i.ResolvedAt == null)
            .OrderBy(i => i.TaskId == null).ThenBy(i => i.HostName).ThenBy(i => i.ServiceDescription).ToListAsync(ct);
        var recent = await db.NagiosIncidents.AsNoTracking().Include(i => i.Task)
            .Where(i => i.NagiosInstanceId == id && i.ResolvedAt != null)
            .OrderByDescending(i => i.ResolvedAt).Take(RecentIncidents).ToListAsync(ct);
        var (online, queries) = AgentState(view.Instance.AgentId);
        return new NagiosInstanceDetails(view.Instance, view.HasPassword, online, queries, open, recent);
    }

    /// <summary>The active agents, each with whether it is connected and announces <see cref="AgentCapabilities.NagiosQuery"/>.</summary>
    public async Task<IReadOnlyList<NagiosAgentChoice>> AgentsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.Status == AgentStatus.Active).OrderBy(a => a.Name).ToListAsync(ct);
        return agents.Select(a =>
        {
            var (online, queries) = AgentState(a.Id);
            return new NagiosAgentChoice(a, online, queries);
        }).ToList();
    }

    /// <summary>
    /// The form's Assignees search: the people a task in the department may be assigned to (§6.2.3), by name or email. Its own
    /// search rather than the task forms', which answers only people who work with tasks themselves.
    /// </summary>
    public async Task<IReadOnlyList<UserSummary>> SearchAssigneesAsync(Guid? departmentId, string? query, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var everywhere = await RoleResolver.UserIdsWithScopeAllAsync(db, Permission.TasksView, ct);
        var q = UserDirectoryService.Assignable(db.Users.AsNoTracking().Include(u => u.Department), departmentId, everywhere);
        var text = query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var anywhere = $"%{text}%";
            var prefix = $"{text}%";
            q = q.Where(u => EF.Functions.ILike(u.DisplayName, anywhere) || EF.Functions.ILike(u.Email!, anywhere))
                .OrderBy(u => EF.Functions.ILike(u.DisplayName, prefix) ? 0 : 1).ThenBy(u => u.DisplayName);
        }
        else
        {
            q = q.OrderBy(u => u.DisplayName);
        }
        return await UserDirectoryService.ToSummariesAsync(db, await q.Take(20).ToListAsync(ct), ct);
    }

    /// <summary>The people chosen on the form, as the picker's chips.</summary>
    public async Task<IReadOnlyList<UserSummary>> AssigneesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        if (ids.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Include(u => u.Department).Where(u => ids.Contains(u.Id)).OrderBy(u => u.DisplayName).ToListAsync(ct);
        return await UserDirectoryService.ToSummariesAsync(db, users, ct);
    }

    public async Task<NagiosInstance> CreateAsync(NagiosInstanceInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var name = NagiosInstanceRules.RequireName(input.Name);
        await RequireUniqueNameAsync(name, null, ct);
        var (url, username) = NagiosInstanceRules.RequireConnection(input, !string.IsNullOrEmpty(input.NewPassword));
        NagiosInstanceRules.RequireRules(input);
        var (agentId, departmentId) = await RequireTargetsAsync(input, ct);
        var people = await AssigneeResolver.ResolveAsync(db, [], input.AssigneeIds.Distinct().ToList(), departmentId, false, ct);

        var now = DateTime.UtcNow;
        var instance = new NagiosInstance
        {
            Name = name, BaseUrl = url, Username = username, PasswordProtected = passwords.Protect(input.NewPassword!),
            AgentId = agentId, DepartmentId = departmentId,
            CreatedAt = now, CreatedById = actor.UserId, UpdatedAt = now, UpdatedById = actor.UserId
        };
        Apply(instance, input);
        foreach (var person in people.Added)
            instance.Assignees.Add(new NagiosInstanceAssignee { NagiosInstanceId = instance.Id, UserId = person.Id });
        db.NagiosInstances.Add(instance);
        audit.Add(actor, AuditEntity.NagiosInstance, instance.Id, AuditAction.Created, null, instance.Name, Describe(instance));
        await db.SaveChangesAsync(ct);
        return instance;
    }

    public async Task<NagiosInstance> UpdateAsync(Guid id, NagiosInstanceInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var instance = await db.NagiosInstances.Include(n => n.Assignees).FirstOrDefaultAsync(n => n.Id == id, ct)
            ?? throw new NotFoundException("Nagios instance not found.");
        var name = NagiosInstanceRules.RequireName(input.Name);
        if (!string.Equals(name, instance.Name, StringComparison.OrdinalIgnoreCase)) await RequireUniqueNameAsync(name, id, ct);
        var newPassword = !string.IsNullOrEmpty(input.NewPassword);
        var (url, username) = NagiosInstanceRules.RequireConnection(input, newPassword || !string.IsNullOrEmpty(instance.PasswordProtected));
        NagiosInstanceRules.RequireRules(input);
        var (agentId, departmentId) = await RequireTargetsAsync(input, ct);
        var current = instance.Assignees.Select(a => a.UserId).ToList();
        var people = await AssigneeResolver.ResolveAsync(db, current, input.AssigneeIds.Distinct().ToList(), departmentId, departmentId != instance.DepartmentId, ct);
        var wanted = current.Except(people.Removed).Concat(people.Added.Select(u => u.Id)).ToList();

        var changes = new ChangeSet()
            .TrackText("name", instance.Name, name)
            .TrackText("baseUrl", instance.BaseUrl, url)
            .TrackText("username", instance.Username, username)
            .Track("validateCertificate", instance.ValidateCertificate, input.ValidateCertificate)
            .Track("agentId", instance.AgentId, agentId)
            .Track("enabled", instance.Enabled, input.Enabled)
            .Track("checkIntervalMinutes", instance.CheckIntervalMinutes, input.CheckIntervalMinutes)
            .Track("hostThresholdMinutes", instance.HostThresholdMinutes, input.HostThresholdMinutes)
            .Track("serviceThresholdMinutes", instance.ServiceThresholdMinutes, input.ServiceThresholdMinutes)
            .Track("raiseHostDown", instance.RaiseHostDown, input.RaiseHostDown)
            .Track("raiseHostUnreachable", instance.RaiseHostUnreachable, input.RaiseHostUnreachable)
            .Track("raiseServiceCritical", instance.RaiseServiceCritical, input.RaiseServiceCritical)
            .Track("raiseServiceWarning", instance.RaiseServiceWarning, input.RaiseServiceWarning)
            .Track("raiseServiceUnknown", instance.RaiseServiceUnknown, input.RaiseServiceUnknown)
            .Track("skipScheduledDowntime", instance.SkipScheduledDowntime, input.SkipScheduledDowntime)
            .Track("skipAcknowledged", instance.SkipAcknowledged, input.SkipAcknowledged)
            .Track("maxNewTasksPerCheck", instance.MaxNewTasksPerCheck, input.MaxNewTasksPerCheck)
            .Track("departmentId", instance.DepartmentId, departmentId)
            .Track("taskPriority", instance.TaskPriority, input.TaskPriority)
            .Track("assigneeIds", Ids(current), Ids(wanted));
        // The audit trail records that the password changed, never what it changed to.
        var details = new Dictionary<string, object?>(changes.Changes);
        if (newPassword) details["password"] = "changed";
        if (details.Count == 0) return instance;

        // Another address may be another Nagios with another clock: its first answer must not be dropped as "not newer".
        if (changes.Contains("baseUrl")) instance.LastQueryTime = null;
        instance.Name = name;
        instance.BaseUrl = url;
        instance.Username = username;
        if (newPassword) instance.PasswordProtected = passwords.Protect(input.NewPassword!);
        instance.AgentId = agentId;
        instance.DepartmentId = departmentId;
        Apply(instance, input);
        foreach (var gone in instance.Assignees.Where(a => people.Removed.Contains(a.UserId)).ToList())
            instance.Assignees.Remove(gone);
        foreach (var person in people.Added)
            instance.Assignees.Add(new NagiosInstanceAssignee { NagiosInstanceId = instance.Id, UserId = person.Id });
        instance.UpdatedAt = DateTime.UtcNow;
        instance.UpdatedById = actor.UserId;
        audit.Add(actor, AuditEntity.NagiosInstance, instance.Id, AuditAction.Updated, null, instance.Name, details);
        await db.SaveChangesAsync(ct);
        return instance;
    }

    /// <summary>The instance and its incidents go; the tasks it raised are ordinary tasks and stay.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var instance = await db.NagiosInstances.FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new NotFoundException("Nagios instance not found.");
        db.NagiosInstances.Remove(instance);
        audit.Add(actor, AuditEntity.NagiosInstance, instance.Id, AuditAction.Deleted, null, instance.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// "Test connection" works on what is typed in the form, so settings can be proven before they're saved; a blank password
    /// means "the one already stored". It reads Nagios through the chosen agent and says what the next check would do about each
    /// problem with these settings - for a saved instance, counting the incidents it already has. Nothing is saved or raised.
    /// </summary>
    public async Task<NagiosTestResult> TestAsync(Guid? id, NagiosInstanceInput input, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var stored = id is Guid storedId ? await db.NagiosInstances.AsNoTracking().FirstOrDefaultAsync(n => n.Id == storedId, ct) : null;
        var password = !string.IsNullOrEmpty(input.NewPassword) ? input.NewPassword : passwords.Unprotect(stored?.PasswordProtected);
        var (url, username) = NagiosInstanceRules.RequireConnection(input, !string.IsNullOrEmpty(password));
        NagiosInstanceRules.RequireRules(input);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.AgentId, ct) ?? throw new ValidationException("That agent doesn't exist.");

        var reading = await dispatcher.ReadAsync(agent.Id, agent.Name, url, username, password!, input.ValidateCertificate, NagiosQueries.Test, ct);
        if (reading.Snapshot is not { } snapshot)
            return new NagiosTestResult(reading.Error ?? "Nagios could not be read.", null, 0, 0, [], reading.DurationMs);

        List<NagiosOpenIncident> open = [];
        IReadOnlySet<(string, string)> withOpenTask = new HashSet<(string, string)>();
        // The incidents belong to the address they were seen at: with another address typed, this is a first check.
        if (stored is not null && string.Equals(stored.BaseUrl, url, StringComparison.Ordinal))
        {
            open = await db.NagiosIncidents.AsNoTracking().Where(i => i.NagiosInstanceId == stored.Id && i.ResolvedAt == null)
                .Select(i => new NagiosOpenIncident(i.Id, i.HostName, i.ServiceDescription, i.ClockFrom, i.LastOkAt, i.TaskId != null)).ToListAsync(ct);
            withOpenTask = (await NagiosMonitor.OpenTasksAsync(db, stored.Id, ct)).Keys.ToHashSet();
        }
        var settings = new NagiosRuleSettings(
            input.HostThresholdMinutes, input.ServiceThresholdMinutes,
            input.RaiseHostDown, input.RaiseHostUnreachable, input.RaiseServiceCritical, input.RaiseServiceWarning, input.RaiseServiceUnknown,
            input.SkipScheduledDowntime, input.SkipAcknowledged, input.MaxNewTasksPerCheck);
        var plan = NagiosIncidentRules.Plan(settings, snapshot, open, withOpenTask);
        return new NagiosTestResult(null, snapshot.Version, snapshot.Hosts.Count, snapshot.Services.Count, plan.Decisions, reading.DurationMs);
    }

    /// <summary>
    /// Queues a check of one instance now. It runs in the Nagios job like any other check, so it can't overlap the scheduled one.
    /// </summary>
    public async Task CheckNowAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var instance = await db.NagiosInstances.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new NotFoundException("Nagios instance not found.");
        if (!instance.Enabled)
            throw new ValidationException("This instance is switched off. Enable it to check it; Test connection shows what a check would do without raising anything.");
        var scheduler = await schedulers.GetScheduler(ct);
        if (await scheduler.GetJobDetail(NagiosPollJob.Key, ct) is null)
            throw new ValidationException("The Nagios job is switched off on this server (Jobs:Nagios:Enabled).");
        await scheduler.TriggerJob(NagiosPollJob.Key, new JobDataMap { { NagiosPollJob.InstanceId, id.ToString() } }, ct);
    }

    /// <summary>
    /// What the task page shows about the Nagios problem behind a task: the latest incident that raised or joined it. Needs no
    /// nagios.manage - the caller has already shown the task itself, and this says no more than the task's own description does.
    /// </summary>
    public async Task<NagiosTaskLink?> ForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var incident = await db.NagiosIncidents.AsNoTracking().Include(i => i.Instance)
            .Where(i => i.TaskId == taskId).OrderByDescending(i => i.OpenedAt).FirstOrDefaultAsync(ct);
        if (incident is null) return null;
        var url = NagiosCgiRules.TryCgiBase(incident.Instance.BaseUrl, out var cgiBase, out _)
            ? NagiosCgiRules.ExtInfoUrl(cgiBase, incident.HostName, incident.ServiceDescription)
            : null;
        return new NagiosTaskLink(incident.Instance.Name, incident.HostName, incident.ServiceDescription, incident.State, incident.ProblemSince,
            incident.ResolvedAt, incident.Resolution, incident.Output, incident.LastSeenAt, url);
    }

    private static void Apply(NagiosInstance instance, NagiosInstanceInput input)
    {
        instance.ValidateCertificate = input.ValidateCertificate;
        instance.Enabled = input.Enabled;
        instance.CheckIntervalMinutes = input.CheckIntervalMinutes;
        instance.HostThresholdMinutes = input.HostThresholdMinutes;
        instance.ServiceThresholdMinutes = input.ServiceThresholdMinutes;
        instance.RaiseHostDown = input.RaiseHostDown;
        instance.RaiseHostUnreachable = input.RaiseHostUnreachable;
        instance.RaiseServiceCritical = input.RaiseServiceCritical;
        instance.RaiseServiceWarning = input.RaiseServiceWarning;
        instance.RaiseServiceUnknown = input.RaiseServiceUnknown;
        instance.SkipScheduledDowntime = input.SkipScheduledDowntime;
        instance.SkipAcknowledged = input.SkipAcknowledged;
        instance.MaxNewTasksPerCheck = input.MaxNewTasksPerCheck;
        instance.TaskPriority = input.TaskPriority;
    }

    /// <summary>Everything but the password, for the audit entry of a new instance.</summary>
    private static object Describe(NagiosInstance n) => new
    {
        n.Name, n.BaseUrl, n.Username, password = "set", n.ValidateCertificate, n.AgentId, n.Enabled, n.CheckIntervalMinutes,
        n.HostThresholdMinutes, n.ServiceThresholdMinutes,
        n.RaiseHostDown, n.RaiseHostUnreachable, n.RaiseServiceCritical, n.RaiseServiceWarning, n.RaiseServiceUnknown,
        n.SkipScheduledDowntime, n.SkipAcknowledged, n.MaxNewTasksPerCheck, n.DepartmentId, n.TaskPriority,
        assigneeIds = n.Assignees.Select(a => a.UserId).ToList()
    };

    private static string Ids(IEnumerable<Guid> ids) => string.Join(", ", ids.OrderBy(id => id));

    private (bool Online, bool QueriesNagios) AgentState(Guid? agentId) =>
        agentId is Guid id && registry.Find(id) is { } connection
            ? (true, connection.Capabilities.Contains(AgentCapabilities.NagiosQuery))
            : (false, false);

    /// <summary>The agent must exist and be active; the department must exist and still take tasks.</summary>
    private async Task<(Guid AgentId, Guid DepartmentId)> RequireTargetsAsync(NagiosInstanceInput input, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.AgentId, ct) ?? throw new ValidationException("That agent doesn't exist.");
        if (agent.Status != AgentStatus.Active) throw new ValidationException($"Agent \"{agent.Name}\" isn't active.");
        if (input.DepartmentId is not Guid departmentId || departmentId == Guid.Empty) throw new ValidationException("Choose the department the tasks are filed in.");
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct) ?? throw new ValidationException("That department doesn't exist.");
        if (department.IsArchived) throw new ValidationException($"The department \"{department.Name}\" is archived and takes no new tasks.");
        return (agent.Id, department.Id);
    }

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var n = name.ToLowerInvariant();
        if (await db.NagiosInstances.AnyAsync(i => i.Name.ToLower() == n && i.Id != exceptId, ct))
            throw new ValidationException($"There is already a Nagios instance called \"{name}\".");
    }

    private async Task<Actor> RequireAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageNagios(actor), "You don't have permission to manage Nagios monitoring.");
        return actor;
    }
}

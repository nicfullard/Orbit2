using Orbit.Data.Entities;

namespace Orbit.Application.Nagios;

/// <summary>The settings of a Nagios instance that decide what raises a task (§6.21).</summary>
public sealed record NagiosRuleSettings(
    int HostThresholdMinutes, int ServiceThresholdMinutes,
    bool RaiseHostDown, bool RaiseHostUnreachable, bool RaiseServiceCritical, bool RaiseServiceWarning, bool RaiseServiceUnknown,
    bool SkipScheduledDowntime, bool SkipAcknowledged, int MaxNewTasksPerCheck)
{
    public static NagiosRuleSettings From(NagiosInstance n) => new(
        n.HostThresholdMinutes, n.ServiceThresholdMinutes,
        n.RaiseHostDown, n.RaiseHostUnreachable, n.RaiseServiceCritical, n.RaiseServiceWarning, n.RaiseServiceUnknown,
        n.SkipScheduledDowntime, n.SkipAcknowledged, n.MaxNewTasksPerCheck);

    public bool Raises(NagiosProblemState state) => state switch
    {
        NagiosProblemState.Down => RaiseHostDown,
        NagiosProblemState.Unreachable => RaiseHostUnreachable,
        NagiosProblemState.Critical => RaiseServiceCritical,
        NagiosProblemState.Warning => RaiseServiceWarning,
        NagiosProblemState.Unknown => RaiseServiceUnknown,
        _ => false
    };

    public TimeSpan Threshold(NagiosProblem problem) =>
        TimeSpan.FromMinutes(Math.Max(0, problem.IsService ? ServiceThresholdMinutes : HostThresholdMinutes));
}

/// <summary>An incident that is still open, as the rules need it.</summary>
/// <param name="ClockFrom">Where its threshold is counted from (Nagios' clock).</param>
/// <param name="LastOkAt">Nagios' "last time up / OK" when it opened.</param>
/// <param name="HasTask">A task was raised for it, or it joined one. It never raises again.</param>
public sealed record NagiosOpenIncident(Guid Id, string Host, string Service, DateTime ClockFrom, DateTime? LastOkAt, bool HasTask);

/// <summary>What a check does about one problem Nagios reports.</summary>
public enum NagiosVerdict
{
    /// <summary>Past its threshold: a task is created.</summary>
    Raise,
    /// <summary>Past its threshold, and an earlier incident's task for the same object is still open: that task gets a note instead.</summary>
    Join,
    /// <summary>Counts towards its threshold but has not reached it.</summary>
    Waiting,
    /// <summary>Past its threshold, but this check has already raised as many tasks as the instance allows; a later check raises it.</summary>
    HeldBack,
    /// <summary>A task was raised for this incident already.</summary>
    AlreadyRaised,
    /// <summary>Nagios is still rechecking it (a soft state).</summary>
    Soft,
    /// <summary>Its state is not one the instance raises tasks for.</summary>
    NotSelected,
    /// <summary>A service whose host is itself down, unreachable or unchecked: the host is the problem.</summary>
    HostDown,
    /// <summary>In scheduled downtime - its own, or its host's.</summary>
    Downtime,
    Acknowledged
}

/// <param name="IncidentId">The open incident it continues; null when it has none yet.</param>
/// <param name="OpensIncident">It gets an incident row at this check.</param>
/// <param name="ClockFrom">Where its threshold is counted from after this check.</param>
/// <param name="RaisesAt">For <see cref="NagiosVerdict.Waiting"/>: when it reaches its threshold if nothing changes (Nagios' clock).</param>
public sealed record NagiosDecision(NagiosProblem Problem, NagiosVerdict Verdict, Guid? IncidentId, bool OpensIncident, DateTime ClockFrom, DateTime? RaisesAt = null);

/// <param name="FailedAgain">Nagios says it was well since the incident opened but it is a problem again now: it gets a fresh incident at this check.</param>
public sealed record NagiosResolve(Guid IncidentId, NagiosResolution Resolution, bool FailedAgain = false);

/// <summary>What one check changes: the incidents to resolve - saved first, since an object may open a new one - and a verdict per problem.</summary>
public sealed record NagiosPlan(IReadOnlyList<NagiosResolve> Resolves, IReadOnlyList<NagiosDecision> Decisions)
{
    public int HeldBack => Decisions.Count(d => d.Verdict == NagiosVerdict.HeldBack);
}

/// <summary>
/// What a reading of a Nagios instance means for its incidents and tasks (spec §6.21). Pure, so every rule is unit-tested: when a
/// problem opens an incident, when the incident raises a task, and when it ends. The one-task-per-down-event guarantee is here -
/// a problem that already has an open incident can only continue it, and an incident with a task never raises again.
/// </summary>
public static class NagiosIncidentRules
{
    /// <summary>Whether an instance is checked at this tick: enabled, and its interval has passed since Nagios was last asked.</summary>
    public static bool IsDue(bool enabled, DateTime? lastAttemptAt, int checkIntervalMinutes, DateTime now) =>
        enabled && (lastAttemptAt is not DateTime last || now - last >= TimeSpan.FromMinutes(Math.Max(1, checkIntervalMinutes)) - Slack);

    /// <summary>The job fires on the minute and the previous attempt was stamped a moment after one, so an exact comparison would skip every other turn.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether a reading may be applied after the last one applied. One that is a little older arrived late - a slow check
    /// overtaken by the next - and is dropped, or it would undo what the newer one did. One that is much older means Nagios'
    /// clock was put back; refusing those would stop the instance for good.
    /// </summary>
    public static bool IsNewer(DateTime? lastApplied, DateTime queryTime) =>
        lastApplied is not DateTime last || queryTime > last || last - queryTime > LateAnswer;

    private static readonly TimeSpan LateAnswer = TimeSpan.FromMinutes(10);

    /// <param name="open">The instance's open incidents.</param>
    /// <param name="objectsWithOpenTask">The hosts and services for which any incident of this instance has a task that is not closed.</param>
    public static NagiosPlan Plan(NagiosRuleSettings settings, NagiosSnapshot snapshot, IReadOnlyCollection<NagiosOpenIncident> open,
        IReadOnlySet<(string Host, string Service)> objectsWithOpenTask)
    {
        var problems = new Dictionary<(string, string), NagiosProblem>();
        foreach (var problem in snapshot.Problems) problems.TryAdd((problem.Host, problem.Service), problem);

        // --- which open incidents end
        var resolves = new List<NagiosResolve>();
        var continuing = new Dictionary<(string, string), NagiosOpenIncident>();
        foreach (var incident in open)
        {
            var key = (incident.Host, incident.Service);
            var status = snapshot.StatusOf(incident.Host, incident.Service);
            problems.TryGetValue(key, out var problem);
            if (status is null)
                resolves.Add(new NagiosResolve(incident.Id, NagiosResolution.Vanished));
            else if (status == NagiosObjectStatus.Ok && problem is null)
                resolves.Add(new NagiosResolve(incident.Id, NagiosResolution.Recovered));
            else if (status == NagiosObjectStatus.Problem && problem is not null && WasOkSince(incident, problem))
                resolves.Add(new NagiosResolve(incident.Id, NagiosResolution.Recovered, FailedAgain: true));
            else
                // Still the same problem - or pending, or the two lists disagree because it changed between them: the next check decides.
                continuing[key] = incident;
        }

        // --- what each reported problem does
        var decisions = new List<NagiosDecision>();
        foreach (var (key, problem) in problems)
        {
            // Only a problem both lists agree on: one that recovered between the two queries is left to the next check.
            if (snapshot.StatusOf(problem.Host, problem.Service) != NagiosObjectStatus.Problem) continue;
            continuing.TryGetValue(key, out var incident);

            if (Ineligible(problem, settings, snapshot) is NagiosVerdict why)
            {
                // A problem that could not raise a task now starts its threshold again when it can. One that never could - still
                // being rechecked, or a state nobody ticked - needs no incident for that: its own "since" restarts when its state changes.
                var opens = incident is null && why is not (NagiosVerdict.Soft or NagiosVerdict.NotSelected);
                decisions.Add(new NagiosDecision(problem, why, incident?.Id, opens, snapshot.QueryTime));
                continue;
            }

            var clockFrom = incident?.ClockFrom ?? problem.Since;
            if (incident is { HasTask: true })
            {
                decisions.Add(new NagiosDecision(problem, NagiosVerdict.AlreadyRaised, incident.Id, false, clockFrom));
                continue;
            }
            var raisesAt = clockFrom + settings.Threshold(problem);
            var verdict = snapshot.QueryTime < raisesAt ? NagiosVerdict.Waiting
                : objectsWithOpenTask.Contains(key) ? NagiosVerdict.Join
                : NagiosVerdict.Raise;
            decisions.Add(new NagiosDecision(problem, verdict, incident?.Id, incident is null, clockFrom, verdict == NagiosVerdict.Waiting ? raisesAt : null));
        }

        // --- the brake: hosts before their services' noise, then whatever has been down longest
        var allowed = decisions.Where(d => d.Verdict == NagiosVerdict.Raise)
            .OrderBy(d => d.Problem.IsService).ThenBy(d => d.ClockFrom)
            .ThenBy(d => d.Problem.Host, StringComparer.Ordinal).ThenBy(d => d.Problem.Service, StringComparer.Ordinal)
            .Take(Math.Max(1, settings.MaxNewTasksPerCheck)).ToHashSet();
        for (var i = 0; i < decisions.Count; i++)
        {
            if (decisions[i].Verdict == NagiosVerdict.Raise && !allowed.Contains(decisions[i]))
                decisions[i] = decisions[i] with { Verdict = NagiosVerdict.HeldBack };
        }

        return new NagiosPlan(resolves, Ordered(decisions));
    }

    /// <summary>Why a problem can't raise a task at this check, or null when it can.</summary>
    private static NagiosVerdict? Ineligible(NagiosProblem problem, NagiosRuleSettings settings, NagiosSnapshot snapshot)
    {
        if (!problem.Hard) return NagiosVerdict.Soft;
        if (!settings.Raises(problem.State)) return NagiosVerdict.NotSelected;
        // Nagios stops checking nothing when a host fails: its services go critical too. The host's own incident covers them.
        if (problem.IsService && snapshot.StatusOf(problem.Host, string.Empty) != NagiosObjectStatus.Ok) return NagiosVerdict.HostDown;
        if (settings.SkipScheduledDowntime && (problem.InDowntime || snapshot.HostsInDowntime.Contains(problem.Host))) return NagiosVerdict.Downtime;
        if (settings.SkipAcknowledged && problem.Acknowledged) return NagiosVerdict.Acknowledged;
        return null;
    }

    /// <summary>
    /// Nagios' "last time up / OK" has moved on since the incident opened, so the object was well in between: the incident Orbit
    /// knows ended, unseen, and this is another one. A value of "never" proves nothing (Nagios forgets it when restarted without
    /// its retained state).
    /// </summary>
    private static bool WasOkSince(NagiosOpenIncident incident, NagiosProblem problem) =>
        problem.LastOkAt is DateTime now && (incident.LastOkAt is not DateTime then || now > then);

    /// <summary>For people: what raises first, then what is counting down, then the rest; hosts before services.</summary>
    private static List<NagiosDecision> Ordered(IEnumerable<NagiosDecision> decisions) =>
        decisions.OrderBy(d => d.Verdict).ThenBy(d => d.Problem.IsService)
            .ThenBy(d => d.Problem.Host, StringComparer.Ordinal).ThenBy(d => d.Problem.Service, StringComparer.Ordinal).ToList();
}

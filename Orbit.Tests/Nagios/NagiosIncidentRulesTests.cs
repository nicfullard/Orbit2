using Orbit.Application.Nagios;
using Orbit.Data.Entities;

namespace Orbit.Tests.Nagios;

/// <summary>
/// What a reading of Nagios means for incidents and tasks (spec §6.21, NAG-004 to NAG-014): when a problem opens an incident, when
/// that raises a task, and what keeps one down event to one task.
/// </summary>
public class NagiosIncidentRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<(string, string)> NoOpenTasks = [];

    /// <summary>Hosts after 15 minutes down, services after 30 critical; downtime and acknowledged problems skipped; ten tasks a check.</summary>
    private static readonly NagiosRuleSettings Settings = new(
        HostThresholdMinutes: 15, ServiceThresholdMinutes: 30,
        RaiseHostDown: true, RaiseHostUnreachable: false, RaiseServiceCritical: true, RaiseServiceWarning: false, RaiseServiceUnknown: false,
        SkipScheduledDowntime: true, SkipAcknowledged: true, MaxNewTasksPerCheck: 10);

    private static NagiosProblem Host(string name, int minutes, NagiosProblemState state = NagiosProblemState.Down, bool hard = true,
        bool acknowledged = false, bool downtime = false, DateTime? lastOk = null) =>
        new(name, string.Empty, state, hard, Now.AddMinutes(-minutes), lastOk ?? Now.AddMinutes(-minutes - 1), acknowledged, downtime, "PING CRITICAL");

    private static NagiosProblem Service(string host, string service, int minutes, NagiosProblemState state = NagiosProblemState.Critical, bool hard = true,
        bool acknowledged = false, bool downtime = false, DateTime? lastOk = null) =>
        new(host, service, state, hard, Now.AddMinutes(-minutes), lastOk ?? Now.AddMinutes(-minutes - 1), acknowledged, downtime, "CRITICAL: broken");

    /// <summary>
    /// A reading in which every problem is in both lists, a problem service's host is up unless the host is a problem too, and
    /// <paramref name="well"/> names further hosts ("host") and services ("host/service") that are up or OK.
    /// </summary>
    private static NagiosSnapshot Reading(IEnumerable<NagiosProblem> problems, string[]? well = null, string[]? pending = null,
        string[]? hostsInDowntime = null, DateTime? at = null)
    {
        var list = problems.ToList();
        var hosts = new Dictionary<string, NagiosObjectStatus>();
        var services = new Dictionary<(string, string), NagiosObjectStatus>();
        void Set(string name, NagiosObjectStatus status)
        {
            var slash = name.IndexOf('/');
            if (slash < 0) { hosts[name] = status; return; }
            hosts.TryAdd(name[..slash], NagiosObjectStatus.Ok);
            services[(name[..slash], name[(slash + 1)..])] = status;
        }
        foreach (var name in well ?? []) Set(name, NagiosObjectStatus.Ok);
        foreach (var name in pending ?? []) Set(name, NagiosObjectStatus.Pending);
        foreach (var p in list) Set(p.IsService ? $"{p.Host}/{p.Service}" : p.Host, NagiosObjectStatus.Problem);
        return new NagiosSnapshot(at ?? Now, (at ?? Now).AddSeconds(-5), hosts, services, list, (hostsInDowntime ?? []).ToHashSet());
    }

    private static NagiosOpenIncident Open(NagiosProblem p, DateTime? clockFrom = null, bool hasTask = false, DateTime? lastOk = null) =>
        new(Guid.NewGuid(), p.Host, p.Service, clockFrom ?? p.Since, lastOk ?? p.LastOkAt, hasTask);

    private static NagiosPlan Plan(NagiosSnapshot reading, NagiosOpenIncident[]? open = null, NagiosRuleSettings? settings = null,
        HashSet<(string, string)>? openTasks = null) =>
        NagiosIncidentRules.Plan(settings ?? Settings, reading, open ?? [], openTasks ?? NoOpenTasks);

    private static NagiosDecision Only(NagiosPlan plan) => Assert.Single(plan.Decisions);

    /// <summary>NAG-004: a problem Nagios is still rechecking opens nothing; a settled one opens an incident, and raises a task only once it has lasted past the threshold.</summary>
    [Fact]
    public void A_problem_raises_a_task_once_it_has_outlasted_its_threshold()
    {
        var soft = Only(Plan(Reading([Host("sw01", 60, hard: false)])));
        Assert.Equal((NagiosVerdict.Soft, false), (soft.Verdict, soft.OpensIncident));

        var young = Only(Plan(Reading([Host("sw01", 10)])));
        Assert.Equal((NagiosVerdict.Waiting, true), (young.Verdict, young.OpensIncident));
        Assert.Equal(Now.AddMinutes(-10), young.ClockFrom);
        Assert.Equal(Now.AddMinutes(5), young.RaisesAt);

        var due = Only(Plan(Reading([Host("sw01", 15)])));
        Assert.Equal((NagiosVerdict.Raise, true), (due.Verdict, due.OpensIncident));

        // The same incident five minutes later, seen first at ten minutes: the clock runs from when Nagios says it went down.
        var waiting = Host("sw01", 16);
        var later = Only(Plan(Reading([waiting]), [Open(waiting)]));
        Assert.Equal((NagiosVerdict.Raise, false), (later.Verdict, later.OpensIncident));
        Assert.NotNull(later.IncidentId);
    }

    /// <summary>NAG-004: hosts and services have their own threshold.</summary>
    [Fact]
    public void Hosts_and_services_have_their_own_threshold()
    {
        var plan = Plan(Reading([Host("sw01", 20), Service("erp01", "Invoices", 20), Service("erp01", "Stock", 30)]));
        Assert.Equal(NagiosVerdict.Raise, plan.Decisions.Single(d => d.Problem.Host == "sw01").Verdict);
        Assert.Equal(NagiosVerdict.Waiting, plan.Decisions.Single(d => d.Problem.Service == "Invoices").Verdict);
        Assert.Equal(NagiosVerdict.Raise, plan.Decisions.Single(d => d.Problem.Service == "Stock").Verdict);
    }

    /// <summary>NAG-005: an incident that has a task never raises again - check after check, for as long as the problem lasts.</summary>
    [Fact]
    public void An_incident_with_a_task_is_never_raised_again()
    {
        var down = Host("sw01", 600);
        var incident = Open(down, hasTask: true);
        for (var check = 0; check < 5; check++)
        {
            var plan = Plan(Reading([down], at: Now.AddMinutes(5 * check)), [incident]);
            Assert.Empty(plan.Resolves);
            var decision = Only(plan);
            Assert.Equal((NagiosVerdict.AlreadyRaised, false, incident.Id), (decision.Verdict, decision.OpensIncident, decision.IncidentId));
        }
    }

    /// <summary>NAG-006: only the states ticked on the instance raise tasks, and a state nobody ticked needs no incident.</summary>
    [Fact]
    public void Only_the_chosen_states_raise_tasks()
    {
        NagiosProblem[] problems =
        [
            Host("sw01", 60), Host("sw02", 60, NagiosProblemState.Unreachable),
            Service("erp01", "Critical", 60), Service("erp01", "Warning", 60, NagiosProblemState.Warning), Service("erp01", "Unknown", 60, NagiosProblemState.Unknown)
        ];
        var byDefault = Plan(Reading(problems));
        Assert.Equal(["Critical", "sw01"], byDefault.Decisions.Where(d => d.Verdict == NagiosVerdict.Raise).Select(d => d.Problem.IsService ? d.Problem.Service : d.Problem.Host).Order());
        Assert.All(byDefault.Decisions.Where(d => d.Verdict == NagiosVerdict.NotSelected), d => Assert.False(d.OpensIncident));
        Assert.Equal(3, byDefault.Decisions.Count(d => d.Verdict == NagiosVerdict.NotSelected));

        var everything = Settings with { RaiseHostUnreachable = true, RaiseServiceWarning = true, RaiseServiceUnknown = true };
        Assert.All(Plan(Reading(problems), settings: everything).Decisions, d => Assert.Equal(NagiosVerdict.Raise, d.Verdict));
    }

    /// <summary>NAG-006: a service that worsens into a chosen state is counted from then - Nagios restarts "since" when the state changes.</summary>
    [Fact]
    public void A_state_nobody_chose_does_not_count_towards_the_threshold()
    {
        // Warning for three days, critical for the last five minutes: the incident opens now and waits out the service threshold.
        var worsened = Only(Plan(Reading([Service("erp01", "Disk", 5)])));
        Assert.Equal((NagiosVerdict.Waiting, true), (worsened.Verdict, worsened.OpensIncident));

        // An incident that slips back to a state nobody chose stays open and starts its clock again.
        var warning = Service("erp01", "Disk", 2, NagiosProblemState.Warning);
        var slipped = Only(Plan(Reading([warning]), [Open(warning, clockFrom: Now.AddHours(-1))]));
        Assert.Equal((NagiosVerdict.NotSelected, Now), (slipped.Verdict, slipped.ClockFrom));
    }

    /// <summary>NAG-007: scheduled downtime - the object's own, or its host's - and an acknowledgement in Nagios raise nothing.</summary>
    [Fact]
    public void Downtime_and_acknowledged_problems_are_not_raised()
    {
        Assert.Equal(NagiosVerdict.Downtime, Only(Plan(Reading([Host("cab01", 600, downtime: true)]))).Verdict);
        Assert.Equal(NagiosVerdict.Acknowledged, Only(Plan(Reading([Host("cab01", 600, acknowledged: true)]))).Verdict);
        // The host is up but in downtime: Nagios shows no downtime on the service itself.
        Assert.Equal(NagiosVerdict.Downtime, Only(Plan(Reading([Service("erp01", "Invoices", 600)], hostsInDowntime: ["erp01"]))).Verdict);

        var raiseAnyway = Settings with { SkipScheduledDowntime = false, SkipAcknowledged = false };
        Assert.Equal(NagiosVerdict.Raise, Only(Plan(Reading([Host("cab01", 600, downtime: true, acknowledged: true)]), settings: raiseAnyway)).Verdict);
    }

    /// <summary>NAG-007: a suppressed problem keeps an incident whose clock restarts at each check, so it needs a full threshold once the suppression ends.</summary>
    [Fact]
    public void A_suppressed_problem_starts_its_threshold_again_afterwards()
    {
        var inDowntime = Host("cab01", 600, downtime: true);
        var first = Only(Plan(Reading([inDowntime])));
        Assert.True(first.OpensIncident);
        Assert.Equal(Now, first.ClockFrom);

        // The downtime ended five minutes after that check; ten minutes on, the host is still down.
        var after = Host("cab01", 610);
        var ten = Only(Plan(Reading([after], at: Now.AddMinutes(10)), [Open(after, clockFrom: Now.AddMinutes(5))]));
        Assert.Equal(NagiosVerdict.Waiting, ten.Verdict);
        Assert.Equal(Now.AddMinutes(20), ten.RaisesAt);

        var twenty = Only(Plan(Reading([after], at: Now.AddMinutes(20)), [Open(after, clockFrom: Now.AddMinutes(5))]));
        Assert.Equal(NagiosVerdict.Raise, twenty.Verdict);
    }

    /// <summary>NAG-008: a service on a host that is down, unreachable or unchecked is not raised - the host is the problem - and gets no burst of tasks when the host returns.</summary>
    [Fact]
    public void A_service_is_not_raised_while_its_host_is_not_up()
    {
        var ping = Service("cab01", "PING", 600);
        var hostDown = Plan(Reading([Host("cab01", 600), ping]));
        Assert.Equal(NagiosVerdict.Raise, hostDown.Decisions.Single(d => !d.Problem.IsService).Verdict);
        var suppressed = hostDown.Decisions.Single(d => d.Problem.IsService);
        Assert.Equal((NagiosVerdict.HostDown, true, Now), (suppressed.Verdict, suppressed.OpensIncident, suppressed.ClockFrom));

        Assert.Equal(NagiosVerdict.HostDown, Only(Plan(Reading([ping], pending: ["cab01"]))).Verdict);

        // The host is back; the service still shows its stale critical state from ten hours ago until Nagios rechecks it.
        var hostBack = Only(Plan(Reading([ping], well: ["cab01"], at: Now.AddMinutes(5)), [Open(ping, clockFrom: Now)]));
        Assert.Equal(NagiosVerdict.Waiting, hostBack.Verdict);
        Assert.Equal(Now.AddMinutes(30), hostBack.RaisesAt);
    }

    /// <summary>NAG-009: an incident ends when Nagios reports the object well, or no longer lists it - never merely because it is missing from the problems.</summary>
    [Fact]
    public void An_incident_ends_when_nagios_says_so()
    {
        var host = Open(Host("sw01", 60), hasTask: true);
        var service = Open(Service("erp01", "Invoices", 60), hasTask: true);
        var removed = Open(Service("erp01", "Old check", 60));

        var plan = Plan(Reading([], well: ["sw01", "erp01/Invoices"]), [host, service, removed]);
        Assert.Empty(plan.Decisions);
        Assert.Equal(3, plan.Resolves.Count);
        Assert.Equal(new NagiosResolve(host.Id, NagiosResolution.Recovered), plan.Resolves.Single(r => r.IncidentId == host.Id));
        Assert.Equal(new NagiosResolve(service.Id, NagiosResolution.Recovered), plan.Resolves.Single(r => r.IncidentId == service.Id));
        Assert.Equal(new NagiosResolve(removed.Id, NagiosResolution.Vanished), plan.Resolves.Single(r => r.IncidentId == removed.Id));
    }

    /// <summary>NAG-009: an object Nagios has not checked yet, or one its two lists disagree about, is left to the next check.</summary>
    [Fact]
    public void An_unsettled_object_is_left_alone()
    {
        // After a Nagios restart everything is pending until its first check.
        var host = Open(Host("sw01", 60), hasTask: true);
        Assert.Empty(Plan(Reading([], pending: ["sw01"]), [host]).Resolves);

        // It changed between the two queries: well in the full list, still among the problems - and the other way round.
        var problem = Host("sw01", 60);
        var disagree = new NagiosSnapshot(Now, Now, new Dictionary<string, NagiosObjectStatus> { ["sw01"] = NagiosObjectStatus.Ok },
            new Dictionary<(string, string), NagiosObjectStatus>(), [problem], new HashSet<string>());
        var plan = Plan(disagree, [host]);
        Assert.Empty(plan.Resolves);
        Assert.Empty(plan.Decisions);

        var missingDetail = Reading([problem]) with { Problems = [] };
        Assert.Empty(Plan(missingDetail, [host]).Resolves);
        // And an object with no incident that the lists disagree about opens none.
        Assert.Empty(Plan(disagree).Decisions);
    }

    /// <summary>NAG-010: when Nagios says the object was well since the incident opened, that incident ended unseen and this is another one.</summary>
    [Fact]
    public void A_recovery_between_checks_ends_the_incident_and_starts_another()
    {
        var first = Host("sw01", 600, lastOk: Now.AddMinutes(-601));
        var incident = Open(first, hasTask: true);
        // Up for a while during an agent outage, down again for the last 20 minutes.
        var again = Host("sw01", 20, lastOk: Now.AddMinutes(-21));

        var plan = Plan(Reading([again]), [incident], openTasks: [("sw01", "")]);
        Assert.Equal(new NagiosResolve(incident.Id, NagiosResolution.Recovered, FailedAgain: true), Assert.Single(plan.Resolves));
        var decision = Only(plan);
        Assert.Null(decision.IncidentId);
        Assert.True(decision.OpensIncident);
        // The first incident's task is still open, so the new incident joins it.
        Assert.Equal(NagiosVerdict.Join, decision.Verdict);
    }

    /// <summary>NAG-010: "never" proves nothing - Nagios forgets its last-OK time when restarted without retained state - so the incident carries on.</summary>
    [Fact]
    public void An_unknown_last_ok_time_keeps_the_same_incident()
    {
        var down = Host("sw01", 600);
        var incident = Open(down, hasTask: true);
        var forgotten = down with { LastOkAt = null };
        Assert.Equal(NagiosVerdict.AlreadyRaised, Only(Plan(Reading([forgotten]), [incident])).Verdict);
        Assert.Empty(Plan(Reading([forgotten]), [incident]).Resolves);

        var earlier = down with { LastOkAt = incident.LastOkAt!.Value.AddHours(-1) };
        Assert.Empty(Plan(Reading([earlier]), [incident]).Resolves);

        // An incident opened when it had never been OK, and Nagios now names a time: it was well since.
        var neverOk = Open(forgotten, hasTask: true);
        Assert.Single(Plan(Reading([down]), [neverOk]).Resolves);
    }

    /// <summary>NAG-011: an object that goes down again while its earlier task is still open joins that task; once the task is closed, a new one is raised.</summary>
    [Fact]
    public void A_new_incident_joins_the_task_that_is_still_open()
    {
        var down = Service("erp01", "Invoices", 45);
        var joined = Only(Plan(Reading([down]), openTasks: [("erp01", "Invoices")]));
        Assert.Equal((NagiosVerdict.Join, true), (joined.Verdict, joined.OpensIncident));

        Assert.Equal(NagiosVerdict.Raise, Only(Plan(Reading([down]))).Verdict);
        // Another service's open task is no reason to join.
        Assert.Equal(NagiosVerdict.Raise, Only(Plan(Reading([down]), openTasks: [("erp01", "Stock"), ("erp01", "")])).Verdict);
        // Below the threshold nothing is joined either.
        Assert.Equal(NagiosVerdict.Waiting, Only(Plan(Reading([Service("erp01", "Invoices", 5)]), openTasks: [("erp01", "Invoices")])).Verdict);
    }

    /// <summary>NAG-012: the brake - at most so many new tasks in one check, hosts before services and the longest down first; the rest keep their incident and wait.</summary>
    [Fact]
    public void A_wide_outage_raises_only_so_many_tasks_a_check()
    {
        NagiosProblem[] problems =
        [
            Service("erp01", "A", 500), Service("erp01", "B", 400), Host("sw03", 60), Host("sw01", 90), Host("sw02", 30),
            Service("erp02", "Joins", 300)
        ];
        var plan = Plan(Reading(problems), settings: Settings with { MaxNewTasksPerCheck = 4 }, openTasks: [("erp02", "Joins")]);

        string Name(NagiosDecision d) => d.Problem.IsService ? d.Problem.Service : d.Problem.Host;
        Assert.Equal(["A", "sw01", "sw02", "sw03"], plan.Decisions.Where(d => d.Verdict == NagiosVerdict.Raise).Select(Name).Order());
        var held = Assert.Single(plan.Decisions, d => d.Verdict == NagiosVerdict.HeldBack);
        Assert.Equal("B", Name(held));
        Assert.True(held.OpensIncident);
        Assert.Equal(1, plan.HeldBack);
        // Joining an open task raises nothing new, so it is not counted.
        Assert.Equal(NagiosVerdict.Join, plan.Decisions.Single(d => Name(d) == "Joins").Verdict);

        // The next check raises what was held back: it has an incident and no task.
        var b = problems[1];
        Assert.Equal(NagiosVerdict.Raise, Only(Plan(Reading([b]), [Open(b)], Settings with { MaxNewTasksPerCheck = 4 })).Verdict);
    }

    /// <summary>NAG-013: a reading that is a little older than the last one applied arrived late and is dropped; a clock put back is not a reason to stop for good.</summary>
    [Fact]
    public void A_late_reading_is_dropped()
    {
        Assert.True(NagiosIncidentRules.IsNewer(null, Now));
        Assert.True(NagiosIncidentRules.IsNewer(Now.AddMinutes(-5), Now));
        Assert.False(NagiosIncidentRules.IsNewer(Now, Now));
        Assert.False(NagiosIncidentRules.IsNewer(Now, Now.AddSeconds(-40)));
        Assert.True(NagiosIncidentRules.IsNewer(Now, Now.AddHours(-2)));
    }

    /// <summary>NAG-014: an instance is checked when it is switched on and its interval has passed since Nagios was last asked; a first check is due at once.</summary>
    [Fact]
    public void An_instance_is_due_when_its_interval_has_passed()
    {
        Assert.True(NagiosIncidentRules.IsDue(true, null, 5, Now));
        Assert.False(NagiosIncidentRules.IsDue(false, null, 5, Now));
        Assert.False(NagiosIncidentRules.IsDue(true, Now.AddMinutes(-2), 5, Now));
        Assert.True(NagiosIncidentRules.IsDue(true, Now.AddMinutes(-5), 5, Now));
        // The job fires on the minute; the last attempt was stamped a second or two after one.
        Assert.True(NagiosIncidentRules.IsDue(true, Now.AddMinutes(-5).AddSeconds(3), 5, Now));
        Assert.True(NagiosIncidentRules.IsDue(true, Now.AddSeconds(-58), 1, Now));
        Assert.False(NagiosIncidentRules.IsDue(true, Now.AddSeconds(-58), 2, Now));
    }
}

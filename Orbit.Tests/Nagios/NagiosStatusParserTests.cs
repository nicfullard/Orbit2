using Orbit.Application.Nagios;
using Orbit.Data.Entities;

namespace Orbit.Tests.Nagios;

/// <summary>
/// Reading Nagios Core's statusjson.cgi answers (spec §6.21, NAG-001 to NAG-003). The answers are trimmed captures from a Nagios
/// Core 4.5 instance with the names changed: a controller that is down and in scheduled downtime, an ERP host with a critical
/// check and a flapping warning, and a host not checked yet.
/// </summary>
public class NagiosStatusParserTests
{
    private const long QueryTime = 1791442659000;

    private static string Envelope(string query, string data, int typeCode = 0, string typeText = "Success", string message = "", long lastDataUpdate = QueryTime - 4000) => $$"""
        {
          "format_version": 0,
          "result": {
            "query_time": {{QueryTime}},
            "cgi": "statusjson.cgi",
            "user": "orbit",
            "query": "{{query}}",
            "query_status": "released",
            "program_start": 1791369405000,
            "last_data_update": {{lastDataUpdate}},
            "type_code": {{typeCode}},
            "type_text": "{{typeText}}",
            "message": "{{message}}"
          },
          "data": {{data}}
        }
        """;

    private static readonly string Hosts = Envelope("hostlist", """
        { "selectors": { }, "hostlist": { "cab01.example.test": 4, "erp01.example.test": 2, "sw01.example.test": 2, "new01.example.test": 1 } }
        """);

    private static readonly string Services = Envelope("servicelist", """
        { "selectors": { }, "servicelist": {
            "cab01.example.test": { "PING": 16 },
            "erp01.example.test": { "PING": 2, "Finance - Invoice errors": 16, "Picking value": 4 },
            "sw01.example.test": { "PING": 2 } } }
        """);

    private static readonly string HostProblems = Envelope("hostlist", """
        { "selectors": { "hoststatus": 12 }, "hostlist": {
            "cab01.example.test": {
              "name": "cab01.example.test",
              "plugin_output": "PING CRITICAL - Packet loss = 100%",
              "long_plugin_output": "",
              "status": 4,
              "current_attempt": 10,
              "max_attempts": 10,
              "last_state_change": 1779093577000,
              "last_hard_state_change": 1779093577000,
              "last_time_up": 1779092767000,
              "last_time_down": 1791442454000,
              "state_type": 1,
              "problem_has_been_acknowledged": false,
              "is_flapping": false,
              "scheduled_downtime_depth": 1
            } } }
        """);

    private static readonly string ServiceProblems = Envelope("servicelist", """
        { "selectors": { "service_status": 28 }, "servicelist": {
            "cab01.example.test": {
              "PING": {
                "host_name": "cab01.example.test",
                "description": "PING",
                "plugin_output": "PING CRITICAL - Packet loss = 100%",
                "status": 16,
                "last_state_change": 1779092976000,
                "last_hard_state_change": 1779092976000,
                "last_time_ok": 1779092976000,
                "state_type": 1,
                "problem_has_been_acknowledged": false,
                "scheduled_downtime_depth": 0
              } },
            "erp01.example.test": {
              "Finance - Invoice errors": {
                "host_name": "erp01.example.test",
                "description": "Finance - Invoice errors",
                "plugin_output": "CRITICAL: There are 1 invoice errors, invoices: (INV0001), Query Duration=0.003693 seconds.",
                "status": 16,
                "last_state_change": 1791389120000,
                "last_hard_state_change": 1791389120000,
                "last_time_ok": 1791388939000,
                "state_type": 1,
                "problem_has_been_acknowledged": true,
                "scheduled_downtime_depth": 0
              },
              "Picking value": {
                "host_name": "erp01.example.test",
                "description": "Picking value",
                "plugin_output": "WARNING: Picking value 257373.95 is below the warning tolerance: 500000",
                "status": 4,
                "last_state_change": 1791441628000,
                "last_hard_state_change": 1787747821000,
                "last_time_ok": 0,
                "state_type": 0,
                "problem_has_been_acknowledged": false,
                "is_flapping": true,
                "scheduled_downtime_depth": 0
              } } } }
        """);

    private static readonly string Downtimes = Envelope("downtimelist", """
        { "selectors": { }, "downtimelist": {
            "72": { "downtime_id": 72, "type": 2, "host_name": "cab01.example.test", "start_time": 1790862710000, "end_time": 1793548310000, "fixed": true, "is_in_effect": true, "author": "Nagios Admin", "comment": "Pending" },
            "73": { "downtime_id": 73, "type": 2, "host_name": "sw01.example.test", "start_time": 1793548310000, "end_time": 1793551910000, "fixed": true, "is_in_effect": false, "author": "Nagios Admin", "comment": "Next month" },
            "74": { "downtime_id": 74, "type": 1, "host_name": "erp01.example.test", "service_description": "PING", "start_time": 1790862710000, "end_time": 1793548310000, "fixed": true, "is_in_effect": true, "author": "Nagios Admin", "comment": "One service" } } }
        """);

    private static readonly string Program = Envelope("programstatus", """
        { "programstatus": { "version": "4.5.12", "nagios_pid": 2783525, "daemon_mode": true } }
        """);

    private static string[] Check => [Hosts, Services, HostProblems, ServiceProblems, Downtimes];

    private static NagiosSnapshot Parse(params string[] bodies)
    {
        Assert.True(NagiosStatusParser.TryParse(bodies, out var snapshot, out var error), error);
        return snapshot;
    }

    private static string Refused(params string[] bodies)
    {
        Assert.False(NagiosStatusParser.TryParse(bodies, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error;
    }

    /// <summary>NAG-001: every host and service comes with its status - up, a problem, or not checked yet - and times are Nagios' milliseconds as UTC.</summary>
    [Fact]
    public void Every_host_and_service_is_read_with_its_status()
    {
        var s = Parse(Check);

        Assert.Equal(new DateTime(2026, 10, 8, 6, 57, 39, DateTimeKind.Utc), s.QueryTime);
        Assert.Equal(DateTimeKind.Utc, s.QueryTime.Kind);
        Assert.Equal(s.QueryTime.AddSeconds(-4), s.LastDataUpdate);
        Assert.Null(s.Version);

        Assert.Equal(4, s.Hosts.Count);
        Assert.Equal(NagiosObjectStatus.Problem, s.StatusOf("cab01.example.test", ""));
        Assert.Equal(NagiosObjectStatus.Ok, s.StatusOf("erp01.example.test", ""));
        Assert.Equal(NagiosObjectStatus.Pending, s.StatusOf("new01.example.test", ""));
        Assert.Null(s.StatusOf("gone.example.test", ""));

        Assert.Equal(5, s.Services.Count);
        Assert.Equal(NagiosObjectStatus.Ok, s.StatusOf("erp01.example.test", "PING"));
        Assert.Equal(NagiosObjectStatus.Problem, s.StatusOf("erp01.example.test", "Picking value"));
        Assert.Null(s.StatusOf("erp01.example.test", "ping"));
    }

    /// <summary>NAG-001: a problem carries its state, whether Nagios has settled on it, since when, when it was last well, and what the plugin said.</summary>
    [Fact]
    public void Problems_are_read_in_detail()
    {
        var s = Parse(Check);
        Assert.Equal(4, s.Problems.Count);

        var host = Assert.Single(s.Problems, p => !p.IsService);
        Assert.Equal(("cab01.example.test", NagiosProblemState.Down, true), (host.Host, host.State, host.Hard));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1779093577000).UtcDateTime, host.Since);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1779092767000).UtcDateTime, host.LastOkAt);
        Assert.True(host.InDowntime);
        Assert.False(host.Acknowledged);
        Assert.Equal("PING CRITICAL - Packet loss = 100%", host.Output);

        // The host's downtime does not show on its service: Nagios reports depth 0 there.
        var ping = Assert.Single(s.Problems, p => p.Host == "cab01.example.test" && p.IsService);
        Assert.Equal(NagiosProblemState.Critical, ping.State);
        Assert.False(ping.InDowntime);

        var invoices = Assert.Single(s.Problems, p => p.Service == "Finance - Invoice errors");
        Assert.True(invoices.Acknowledged);
        Assert.Equal("Finance - Invoice errors on erp01.example.test", invoices.Label);

        // A soft state: its last hard change is from before this problem, so "since" is the last state change. 0 reads as never.
        var picking = Assert.Single(s.Problems, p => p.Service == "Picking value");
        Assert.Equal((NagiosProblemState.Warning, false), (picking.State, picking.Hard));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791441628000).UtcDateTime, picking.Since);
        Assert.Null(picking.LastOkAt);
    }

    /// <summary>NAG-001: a host's downtime that is in effect is known by the host's name; a service's downtime and one not yet started are not the host's.</summary>
    [Fact]
    public void Host_downtimes_in_effect_are_read()
    {
        var s = Parse(Check);
        Assert.Equal(["cab01.example.test"], s.HostsInDowntime);
    }

    /// <summary>NAG-001: "Test connection" also asks for the program status, which gives the Nagios version.</summary>
    [Fact]
    public void The_version_comes_from_the_program_status()
    {
        Assert.Equal("4.5.12", Parse([.. Check, Program]).Version);
    }

    /// <summary>NAG-001: a clock that is ahead on the Nagios host can't make a problem start in the future.</summary>
    [Fact]
    public void A_problem_never_starts_after_the_reading()
    {
        var future = HostProblems.Replace("\"last_hard_state_change\": 1779093577000", $"\"last_hard_state_change\": {QueryTime + 60000}");
        var s = Parse(Hosts, Services, future, ServiceProblems, Downtimes);
        Assert.Equal(s.QueryTime, s.Problems.Single(p => !p.IsService).Since);
    }

    /// <summary>NAG-002: statusjson.cgi answers a rejected query with HTTP 200 and the reason in the body - that is a failure, with Nagios' words.</summary>
    [Fact]
    public void A_refused_query_is_a_failure_with_the_reason()
    {
        var refused = Envelope("servicelist", """{ "options": { } }""", typeCode: 6, typeText: "Option Value Invalid", message: "The host 'NOPE' could not be found.");
        var error = Refused(Hosts, refused, HostProblems, ServiceProblems, Downtimes);
        Assert.Contains("service list", error);
        Assert.Contains("The host 'NOPE' could not be found.", error);
    }

    /// <summary>NAG-002: anything that is not the expected report is refused rather than read as "no problems".</summary>
    [Fact]
    public void An_answer_that_is_not_a_status_report_is_refused()
    {
        Refused(Hosts, Services, HostProblems, ServiceProblems);
        Refused(Hosts, Services, HostProblems, ServiceProblems, "<html>Sign in</html>");
        Refused(Hosts, Services, HostProblems, ServiceProblems, "[]");
        Refused(Hosts, Services, HostProblems, ServiceProblems, """{ "format_version": 0 }""");
        // The right envelope with the list missing, or not a list.
        Refused(Hosts, Services, HostProblems, ServiceProblems, Envelope("downtimelist", """{ "selectors": { } }"""));
        Refused(Hosts, Services, Envelope("hostlist", """{ "hostlist": [] }"""), ServiceProblems, Downtimes);
        // Answers in the wrong order: the service list where the host list belongs.
        Refused(Services, Hosts, HostProblems, ServiceProblems, Downtimes);
    }

    /// <summary>NAG-002: a Nagios that lists no hosts is far likelier hiding them from Orbit's user than watching nothing; acting on it would close every incident.</summary>
    [Fact]
    public void An_empty_host_list_is_refused()
    {
        var none = Envelope("hostlist", """{ "selectors": { }, "hostlist": { } }""");
        Assert.Contains("authorised", Refused(none, Services, HostProblems, ServiceProblems, Downtimes));
    }

    /// <summary>NAG-003: status data Nagios stopped updating is not acted on; the age is measured on Nagios' own clock.</summary>
    [Fact]
    public void Stale_status_data_fails_the_check()
    {
        Assert.Null(NagiosStatusParser.Staleness(Parse(Check), 300));

        var old = Envelope("hostlist", """{ "hostlist": { "erp01.example.test": 2 } }""", lastDataUpdate: QueryTime - 20 * 60 * 1000);
        var stale = Parse(old, Services, HostProblems, ServiceProblems, Downtimes);
        Assert.Contains("20 minutes", NagiosStatusParser.Staleness(stale, 300));
        Assert.Null(NagiosStatusParser.Staleness(stale, 1800));
    }
}

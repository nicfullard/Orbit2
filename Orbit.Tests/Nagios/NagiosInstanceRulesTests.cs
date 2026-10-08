using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Nagios;
using Orbit.Data.Entities;

namespace Orbit.Tests.Nagios;

/// <summary>
/// A Nagios instance's address and settings, and the words Orbit writes about a problem (spec §6.21, NAG-015 to NAG-017). The
/// address rules are shared with the agent, which enforces them: it only ever calls statusjson.cgi.
/// </summary>
public class NagiosInstanceRulesTests
{
    private static Uri CgiBase(string url)
    {
        Assert.True(NagiosCgiRules.TryCgiBase(url, out var cgiBase, out var error), error);
        return cgiBase;
    }

    /// <summary>NAG-015: the address Nagios is opened with gets cgi-bin/ added; one that already names a cgi-bin folder, or a CGI in it, is taken as it stands.</summary>
    [Fact]
    public void The_cgi_folder_is_worked_out_from_the_address()
    {
        Assert.Equal("http://10.184.1.20/nagios/cgi-bin/", CgiBase("http://10.184.1.20/nagios/").AbsoluteUri);
        Assert.Equal("http://10.184.1.20/nagios/cgi-bin/", CgiBase("  http://10.184.1.20/nagios  ").AbsoluteUri);
        Assert.Equal("https://nagios.example.test:8443/cgi-bin/", CgiBase("https://nagios.example.test:8443").AbsoluteUri);
        Assert.Equal("http://nagios.example.test/nagios/cgi-bin/", CgiBase("http://nagios.example.test/nagios/cgi-bin").AbsoluteUri);
        Assert.Equal("http://nagios.example.test/nagios/cgi-bin/", CgiBase("http://nagios.example.test/nagios/cgi-bin/statusjson.cgi").AbsoluteUri);
        Assert.Equal("http://nagios.example.test/cgi-bin/nagios4/", CgiBase("http://nagios.example.test/cgi-bin/nagios4/").AbsoluteUri);
    }

    /// <summary>NAG-015: only a plain http or https address - no sign-in in it, no query, nothing else.</summary>
    [Fact]
    public void An_address_that_is_not_a_plain_web_address_is_refused()
    {
        foreach (var bad in new[]
                 {
                     null, "", "   ", "nagios.example.test/nagios", "ftp://nagios.example.test/nagios/", "file:///etc/passwd",
                     "http://admin:secret@nagios.example.test/nagios/", "http://nagios.example.test/nagios/?x=1", "http://nagios.example.test/nagios/#top",
                     "http://nagios.example.test/" + new string('a', 500)
                 })
        {
            Assert.False(NagiosCgiRules.TryCgiBase(bad, out _, out var error), bad);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireUrl(bad));
        }
        Assert.Equal("http://10.184.1.20/nagios/", NagiosInstanceRules.RequireUrl(" http://10.184.1.20/nagios/ "));
    }

    /// <summary>NAG-015: every query Orbit asks goes to statusjson.cgi with its query string exactly as written - a plus sign separates states and must stay one.</summary>
    [Fact]
    public void A_status_query_keeps_its_query_string()
    {
        var cgiBase = CgiBase("http://10.184.1.20/nagios/");
        foreach (var query in NagiosQueries.Test)
        {
            Assert.True(NagiosCgiRules.TryStatusUrl(cgiBase, query, out var url, out var error), error);
            Assert.Equal($"http://10.184.1.20/nagios/cgi-bin/statusjson.cgi?{query}", url.AbsoluteUri);
        }
        Assert.Contains("hoststatus=down+unreachable", NagiosQueries.HostProblems);
        Assert.Contains("servicestatus=warning+critical+unknown", NagiosQueries.ServiceProblems);
        Assert.Equal(5, NagiosQueries.Check.Count);
    }

    /// <summary>NAG-015: nothing but a status query gets through - no other CGI, no path, no second address - so the agent cannot be pointed at cmd.cgi.</summary>
    [Fact]
    public void Anything_that_is_not_a_status_query_is_refused()
    {
        var cgiBase = CgiBase("http://10.184.1.20/nagios/");
        foreach (var bad in new[]
                 {
                     null, "", "hostlist", "cmd_typ=33&host=sw01", "query=hostlist#x", "query=hostlist&x=../cmd.cgi", "query=hostlist&u=http://evil.example.test/",
                     "query=hostlist\r\nX-Injected: 1", "query=host list", "query=hostlist?x", "query=" + new string('a', 500)
                 })
        {
            Assert.False(NagiosCgiRules.TryStatusUrl(cgiBase, bad, out _, out _), bad);
        }
    }

    /// <summary>NAG-015: the link a person follows from the task to Nagios names the host, and the service when there is one, safely encoded.</summary>
    [Fact]
    public void The_link_to_nagios_names_the_host_and_service()
    {
        var cgiBase = CgiBase("http://10.184.1.20/nagios/");
        Assert.Equal("http://10.184.1.20/nagios/cgi-bin/extinfo.cgi?type=1&host=sw01.example.test", NagiosCgiRules.ExtInfoUrl(cgiBase, "sw01.example.test", ""));
        Assert.Equal("http://10.184.1.20/nagios/cgi-bin/extinfo.cgi?type=2&host=erp01&service=Finance%20-%20Invoices%20%26%20EDI",
            NagiosCgiRules.ExtInfoUrl(cgiBase, "erp01", "Finance - Invoices & EDI"));
    }

    private static NagiosInstanceInput Input(Action<NagiosInstanceInput>? change = null)
    {
        var input = new NagiosInstanceInput
        {
            Name = "Head office", BaseUrl = "http://10.184.1.20/nagios/", Username = "orbit", NewPassword = "secret",
            AgentId = Guid.NewGuid(), DepartmentId = Guid.NewGuid()
        };
        change?.Invoke(input);
        return input;
    }

    /// <summary>NAG-016: an instance needs a name, an address, a username without a colon, a password - typed or already stored - and an agent.</summary>
    [Fact]
    public void An_instance_needs_its_connection_settings()
    {
        Assert.Equal(("http://10.184.1.20/nagios/", "orbit"), NagiosInstanceRules.RequireConnection(Input(i => i.Username = " orbit "), hasPassword: true));
        Assert.Equal("Head office", NagiosInstanceRules.RequireName("  Head office "));

        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireName(" "));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireName(new string('n', 101)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.BaseUrl = "nagios"), true));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.Username = ""), true));
        // HTTP basic authentication joins the username and password with a colon.
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.Username = "orbit:x"), true));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.NewPassword = new string('p', 201)), true));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(), hasPassword: false));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.AgentId = null), true));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireConnection(Input(i => i.AgentId = Guid.Empty), true));
    }

    /// <summary>NAG-016: the thresholds and limits are in range, and at least one state raises a task - an instance that raises nothing is a mistake.</summary>
    [Fact]
    public void An_instance_needs_sensible_rules()
    {
        NagiosInstanceRules.RequireRules(Input());
        NagiosInstanceRules.RequireRules(Input(i => { i.HostThresholdMinutes = 0; i.ServiceThresholdMinutes = 43200; i.CheckIntervalMinutes = 1440; i.MaxNewTasksPerCheck = 100; }));

        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.CheckIntervalMinutes = 0)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.CheckIntervalMinutes = 1441)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.HostThresholdMinutes = -1)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.ServiceThresholdMinutes = 43201)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.MaxNewTasksPerCheck = 0)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.MaxNewTasksPerCheck = 101)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => i.TaskPriority = (TaskPriority)99)));
        Assert.Throws<ValidationException>(() => NagiosInstanceRules.RequireRules(Input(i => { i.RaiseHostDown = false; i.RaiseServiceCritical = false; })));
        NagiosInstanceRules.RequireRules(Input(i => { i.RaiseHostDown = false; i.RaiseServiceCritical = false; i.RaiseServiceWarning = true; }));
    }

    /// <summary>NAG-016: a new instance raises tasks for down hosts and critical services, and skips downtime and acknowledged problems.</summary>
    [Fact]
    public void A_new_instance_starts_with_down_hosts_and_critical_services()
    {
        var settings = NagiosRuleSettings.From(new NagiosInstance());
        Assert.True(settings.Raises(NagiosProblemState.Down));
        Assert.True(settings.Raises(NagiosProblemState.Critical));
        Assert.False(settings.Raises(NagiosProblemState.Unreachable));
        Assert.False(settings.Raises(NagiosProblemState.Warning));
        Assert.False(settings.Raises(NagiosProblemState.Unknown));
        Assert.True(settings.SkipScheduledDowntime);
        Assert.True(settings.SkipAcknowledged);
        Assert.Equal(10, settings.MaxNewTasksPerCheck);
        Assert.False(new NagiosInstance().Enabled);
        Assert.Equal(TaskPriority.High, new NagiosInstance().TaskPriority);
    }

    private static readonly DateTime Since = new(2026, 10, 7, 21, 25, 0, DateTimeKind.Utc);

    /// <summary>NAG-017: the task says what is down, since when and what Nagios said, and where to look.</summary>
    [Fact]
    public void A_raised_task_says_what_is_down()
    {
        var host = new NagiosProblem("sw01.example.test", "", NagiosProblemState.Down, true, Since, null, false, false, "PING CRITICAL - Packet loss = 100%");
        Assert.Equal("Nagios: sw01.example.test is DOWN", NagiosText.TaskTitle(host));

        var service = new NagiosProblem("erp01.example.test", "Finance - Invoice errors", NagiosProblemState.Critical, true, Since, null, false, false, "CRITICAL: There are 1 invoice errors");
        Assert.Equal("Nagios: Finance - Invoice errors on erp01.example.test is CRITICAL", NagiosText.TaskTitle(service));

        var description = NagiosText.TaskDescription("Head office", service, "http://nagios.example.test/nagios/cgi-bin/extinfo.cgi?type=2&host=erp01");
        Assert.Contains("\"Head office\"", description);
        Assert.Contains("Host: erp01.example.test\n", description);
        Assert.Contains("Service: Finance - Invoice errors\n", description);
        Assert.Contains("State: CRITICAL since 2026-10-07 21:25 UTC\n", description);
        // The description is a snapshot; the task page shows what Nagios says now beside it.
        Assert.Contains("When raised, Nagios said: CRITICAL: There are 1 invoice errors\n", description);
        Assert.Contains("extinfo.cgi?type=2&host=erp01", description);
        Assert.DoesNotContain("Service:", NagiosText.TaskDescription("Head office", host, null));
    }

    /// <summary>NAG-017: what Nagios supplied is cleaned and clipped - a title the database would refuse, or a control character in a plugin's output, would fail every later check.</summary>
    [Fact]
    public void Text_from_nagios_is_cleaned_and_clipped()
    {
        var noisy = new NagiosProblem("sw01", new string('s', 400), NagiosProblemState.Critical, true, Since, null, false, false, "line one\r\nline\ttwo\0 \u0007end");
        var title = NagiosText.TaskTitle(noisy);
        Assert.Equal(NagiosText.MaxTitleLength, title.Length);
        Assert.EndsWith("...", title);

        Assert.Equal("line one  line two end", NagiosText.Clean(noisy.Output, 1000));
        Assert.DoesNotContain('\0', NagiosText.TaskDescription("Head office", noisy, null));
        Assert.Null(NagiosText.Clean(" \0\t ", 100));
        Assert.Null(NagiosText.Clean(null, 100));
        // A lone surrogate is not text; a real emoji survives, and is never cut in half.
        Assert.Equal("ab", NagiosText.Clean("a\uD83Db", 100));
        Assert.Equal("ok \U0001F600", NagiosText.Clean("ok \U0001F600", 100));
        Assert.Equal("abcd...", NagiosText.Clean("abcd\U0001F600fghij", 8));
    }

    /// <summary>NAG-017: the notes say what Nagios reported - a recovery, an object it no longer lists, a problem that came back - and that the task was left for a person to close.</summary>
    [Fact]
    public void Notes_say_what_nagios_reported()
    {
        var at = new DateTime(2026, 10, 8, 6, 57, 0, DateTimeKind.Utc);
        var recovered = NagiosText.RecoveredNote("Head office", "sw01", "", at, taskOpen: true);
        Assert.Contains("host sw01 UP again, as of 2026-10-08 06:57 UTC", recovered);
        Assert.Contains("leaves the task open", recovered);
        Assert.DoesNotContain("leaves the task open", NagiosText.RecoveredNote("Head office", "sw01", "", at, taskOpen: false));
        Assert.Contains("Invoices on erp01 OK again", NagiosText.RecoveredNote("Head office", "erp01", "Invoices", at, true));

        Assert.Contains("no longer lists host sw01", NagiosText.VanishedNote("Head office", "sw01", ""));
        Assert.DoesNotContain("again, as of", NagiosText.VanishedNote("Head office", "sw01", ""));

        var again = new NagiosProblem("erp01", "Invoices", NagiosProblemState.Critical, true, Since, null, false, false, "CRITICAL: 3 errors");
        var note = NagiosText.DownAgainNote("Head office", again);
        Assert.Contains("Invoices on erp01 CRITICAL again, since 2026-10-07 21:25 UTC", note);
        Assert.Contains("no new one was raised", note);
        Assert.Contains("Nagios said: CRITICAL: 3 errors", note);
    }
}

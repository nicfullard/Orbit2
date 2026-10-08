namespace Orbit.Application.Nagios;

/// <summary>
/// The <c>statusjson.cgi</c> queries Orbit asks an agent to make (§6.21). The plain lists name every host and service with its
/// status and are small; only the problems are asked for in detail, so a check stays a few kilobytes on an instance of any size.
/// </summary>
public static class NagiosQueries
{
    public const string Hosts = "query=hostlist";
    public const string Services = "query=servicelist";
    public const string HostProblems = "query=hostlist&details=true&hoststatus=down+unreachable";
    public const string ServiceProblems = "query=servicelist&details=true&servicestatus=warning+critical+unknown";
    /// <summary>A host's scheduled downtime is not shown on its services, so the downtimes are read as well.</summary>
    public const string Downtimes = "query=downtimelist&details=true";
    public const string Program = "query=programstatus";

    /// <summary>A check. <see cref="NagiosStatusParser"/> reads the answers in this order.</summary>
    public static readonly IReadOnlyList<string> Check = [Hosts, Services, HostProblems, ServiceProblems, Downtimes];

    /// <summary>"Test connection": a check plus the Nagios version.</summary>
    public static readonly IReadOnlyList<string> Test = [.. Check, Program];
}

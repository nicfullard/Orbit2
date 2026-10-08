using Orbit.Data.Entities;

namespace Orbit.Application.Nagios;

/// <summary>What Nagios says about a host or service in its plain lists (§6.21): well, in a problem state, or not checked yet.</summary>
public enum NagiosObjectStatus
{
    /// <summary>A host that is UP or a service that is OK.</summary>
    Ok,
    Problem,
    /// <summary>Not checked yet (after a Nagios restart, say) or a status Orbit doesn't know: neither well nor a problem.</summary>
    Pending
}

/// <summary>A host or service Nagios reports in a problem state, with what the rules need to know about it.</summary>
/// <param name="Service">The service's description; empty for the host itself.</param>
/// <param name="Hard">Nagios has finished retrying and settled on the state. A soft state is still being rechecked.</param>
/// <param name="Since">When the current hard state began, on Nagios' clock.</param>
/// <param name="LastOkAt">Nagios' "last time up / OK"; null when it has never been.</param>
/// <param name="InDowntime">The object's own scheduled downtime is in effect. A host's downtime does not show on its services.</param>
public sealed record NagiosProblem(
    string Host, string Service, NagiosProblemState State, bool Hard, DateTime Since, DateTime? LastOkAt,
    bool Acknowledged, bool InDowntime, string? Output)
{
    public bool IsService => Service.Length > 0;
    public string Label => IsService ? $"{Service} on {Host}" : Host;
}

/// <summary>
/// One reading of a Nagios instance (§6.21), parsed from the answers to <see cref="NagiosQueries.Check"/>. Every host and service
/// is in the plain lists with its status, so "well again" is something Nagios said, never the absence of a problem.
/// </summary>
/// <param name="QueryTime">Nagios' own clock when it answered: durations are measured against it, not Orbit's clock.</param>
/// <param name="LastDataUpdate">When Nagios last wrote its status data; long ago means the daemon has stopped.</param>
/// <param name="Version">From the program status, which only "Test connection" asks for.</param>
public sealed record NagiosSnapshot(
    DateTime QueryTime,
    DateTime? LastDataUpdate,
    IReadOnlyDictionary<string, NagiosObjectStatus> Hosts,
    IReadOnlyDictionary<(string Host, string Service), NagiosObjectStatus> Services,
    IReadOnlyList<NagiosProblem> Problems,
    IReadOnlySet<string> HostsInDowntime,
    string? Version = null)
{
    /// <summary>Null when Nagios no longer lists the host or service at all.</summary>
    public NagiosObjectStatus? StatusOf(string host, string service) =>
        service.Length == 0
            ? Hosts.TryGetValue(host, out var h) ? h : null
            : Services.TryGetValue((host, service), out var s) ? s : null;
}

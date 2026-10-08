using Orbit.Application.Nagios;
using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>Editable settings of a Nagios instance (§6.21). The password is write-only: it never travels back to the page.</summary>
public sealed class NagiosInstanceInput
{
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? Username { get; set; }
    /// <summary>Null or empty keeps the stored password.</summary>
    public string? NewPassword { get; set; }
    public bool ValidateCertificate { get; set; } = true;
    public Guid? AgentId { get; set; }
    public bool Enabled { get; set; }
    public int CheckIntervalMinutes { get; set; } = 5;
    public int HostThresholdMinutes { get; set; } = 15;
    public int ServiceThresholdMinutes { get; set; } = 15;
    public bool RaiseHostDown { get; set; } = true;
    public bool RaiseHostUnreachable { get; set; }
    public bool RaiseServiceCritical { get; set; } = true;
    public bool RaiseServiceWarning { get; set; }
    public bool RaiseServiceUnknown { get; set; }
    public bool SkipScheduledDowntime { get; set; } = true;
    public bool SkipAcknowledged { get; set; } = true;
    public int MaxNewTasksPerCheck { get; set; } = 10;
    public Guid? DepartmentId { get; set; }
    public TaskPriority TaskPriority { get; set; } = TaskPriority.High;
    public List<Guid> AssigneeIds { get; set; } = [];
}

public sealed record NagiosInstanceView(NagiosInstance Instance, bool HasPassword);

/// <summary>An instance on the list: whether its agent can be asked right now, and what it is currently watching.</summary>
/// <param name="Raised">Open incidents that have a task.</param>
public sealed record NagiosInstanceListItem(NagiosInstance Instance, bool AgentOnline, bool AgentQueriesNagios, int OpenIncidents, int Raised);

/// <summary>An agent as the instance form offers it: whether it is connected right now, and whether it is new enough to read Nagios.</summary>
public sealed record NagiosAgentChoice(Agent Agent, bool Online, bool QueriesNagios);

/// <summary>An instance with what it is watching: its open incidents and the ones that ended most recently.</summary>
public sealed record NagiosInstanceDetails(
    NagiosInstance Instance, bool HasPassword, bool AgentOnline, bool AgentQueriesNagios,
    IReadOnlyList<NagiosIncident> Open, IReadOnlyList<NagiosIncident> Recent);

/// <summary>
/// The outcome of "Test connection": what Nagios answered through the agent, and what a first check with the settings on the form
/// would do about each problem it reports. Nothing is saved or raised.
/// </summary>
public sealed record NagiosTestResult(
    string? Error, string? Version, int Hosts, int Services, IReadOnlyList<NagiosDecision> Decisions, long DurationMs)
{
    public bool Ok => Error is null;
}

/// <summary>What the task page shows about the Nagios problem a task was raised for; needs no nagios.manage.</summary>
/// <param name="Output">
/// What the check said at the last check that saw the problem. It can change while the problem lasts - another order joins the
/// list - without anything new being raised, so the task shows this beside the description, which keeps what was said when raised.
/// </param>
/// <param name="LastSeenAt">When that was: how fresh <paramref name="Output"/> is.</param>
/// <param name="Url">The Nagios page about the host or service, for people inside the network.</param>
public sealed record NagiosTaskLink(
    string InstanceName, string Host, string Service, NagiosProblemState State, DateTime ProblemSince,
    DateTime? ResolvedAt, NagiosResolution? Resolution, string? Output, DateTime LastSeenAt, string? Url);

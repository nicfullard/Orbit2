namespace Orbit.Data.Entities;

/// <summary>
/// A Nagios Core instance Orbit watches (spec §6.21). Orbit cannot reach it; an Orbit Agent inside the network reads its status
/// on a schedule, and Orbit raises a task for each host or service that stays down longer than the thresholds here. The sign-in
/// lives in Orbit, encrypted, and travels with each command - the agent keeps nothing.
/// </summary>
public class NagiosInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The address Nagios is opened with, e.g. <c>http://nagios.example/nagios/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    /// <summary>The password, encrypted with the Data Protection key ring. Write-only from the UI's point of view.</summary>
    public string? PasswordProtected { get; set; }
    /// <summary>Off only for an instance behind a certificate the agent's machine doesn't trust.</summary>
    public bool ValidateCertificate { get; set; } = true;
    /// <summary>The agent that can reach this instance. Null only after that agent was deleted, and then nothing is checked.</summary>
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public bool Enabled { get; set; }
    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>How long a host must have been down, in a state that raises tasks, before one is raised.</summary>
    public int HostThresholdMinutes { get; set; } = 15;
    public int ServiceThresholdMinutes { get; set; } = 15;
    public bool RaiseHostDown { get; set; } = true;
    public bool RaiseHostUnreachable { get; set; }
    public bool RaiseServiceCritical { get; set; } = true;
    public bool RaiseServiceWarning { get; set; }
    public bool RaiseServiceUnknown { get; set; }
    /// <summary>A host or service in scheduled downtime raises nothing, as Nagios itself notifies nobody.</summary>
    public bool SkipScheduledDowntime { get; set; } = true;
    /// <summary>A problem someone acknowledged in Nagios raises nothing.</summary>
    public bool SkipAcknowledged { get; set; } = true;
    /// <summary>A brake for a wide outage: at most this many new tasks in one check; the rest follow on later checks.</summary>
    public int MaxNewTasksPerCheck { get; set; } = 10;

    /// <summary>The department the raised tasks are filed in.</summary>
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public TaskPriority TaskPriority { get; set; } = TaskPriority.High;
    /// <summary>Who each raised task is assigned to; nobody means it waits unassigned in the department.</summary>
    public ICollection<NagiosInstanceAssignee> Assignees { get; set; } = new List<NagiosInstanceAssignee>();

    /// <summary>The last time Nagios was asked and answered, well or badly. Not moved when no agent could be reached, so that is retried at once.</summary>
    public DateTime? LastAttemptAt { get; set; }
    /// <summary>The last check that was read in full and applied.</summary>
    public DateTime? LastSucceededAt { get; set; }
    /// <summary>Nagios' own clock at the last applied check: an answer that isn't newer is discarded.</summary>
    public DateTime? LastQueryTime { get; set; }
    /// <summary>Why the last check did nothing; null after a good one.</summary>
    public string? LastError { get; set; }
    /// <summary>Problems past their threshold that the last check held back under <see cref="MaxNewTasksPerCheck"/>.</summary>
    public int HeldBack { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedById { get; set; }

    public ICollection<NagiosIncident> Incidents { get; set; } = new List<NagiosIncident>();
}

/// <summary>One person the tasks a Nagios instance raises are assigned to (§6.21).</summary>
public class NagiosInstanceAssignee
{
    public Guid NagiosInstanceId { get; set; }
    public NagiosInstance Instance { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

/// <summary>
/// One problem period of one host or service on a Nagios instance (§6.21): opened the first time Orbit sees it in a hard problem
/// state, resolved when Nagios reports it well again or stops listing it. It is what keeps a down event to one task - an object
/// has at most one open incident (a unique index), and an incident raises at most once.
/// </summary>
public class NagiosIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NagiosInstanceId { get; set; }
    public NagiosInstance Instance { get; set; } = null!;
    /// <summary>The host's name in Nagios, which is case-sensitive.</summary>
    public string HostName { get; set; } = string.Empty;
    /// <summary>The service's description; empty for the host itself.</summary>
    public string ServiceDescription { get; set; } = string.Empty;
    /// <summary>The state at the last check that saw it.</summary>
    public NagiosProblemState State { get; set; }
    /// <summary>What the check plugin said, at the last check that saw it.</summary>
    public string? Output { get; set; }
    /// <summary>When Nagios says the problem state began (its clock).</summary>
    public DateTime ProblemSince { get; set; }
    /// <summary>
    /// Where the threshold is counted from (Nagios' clock): <see cref="ProblemSince"/> at first, moved to the time of each check
    /// at which the problem could not raise a task - not a chosen state, acknowledged, in downtime, or a service whose host is down.
    /// </summary>
    public DateTime ClockFrom { get; set; }
    /// <summary>Nagios' "last time up / OK" when this incident opened. A later value means it recovered and failed again unseen.</summary>
    public DateTime? LastOkAt { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null while the problem lasts.</summary>
    public DateTime? ResolvedAt { get; set; }
    public NagiosResolution? Resolution { get; set; }
    /// <summary>The task raised for it, or the still-open task of an earlier incident that it joined. Null until the threshold passes.</summary>
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public bool IsService => ServiceDescription.Length > 0;
}

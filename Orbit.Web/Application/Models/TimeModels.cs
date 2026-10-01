using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class TimeEntryInput
{
    public Guid TaskId { get; set; }
    /// <summary>Defaults to the caller. Admins may log on behalf of others.</summary>
    public Guid? UserId { get; set; }
    public DateOnly Date { get; set; }
    public int DurationMinutes { get; set; }
    public string? Note { get; set; }
    /// <summary>MCP <c>log_time</c> only: a retry with the same key returns the entry already logged.</summary>
    public string? IdempotencyKey { get; set; }
}

public sealed record MyTimeSummary(IReadOnlyList<TimeEntry> Entries, int TotalMinutes, DateOnly From, DateOnly To)
{
    public IReadOnlyList<(DateOnly Date, int Minutes)> ByDay =>
        Entries.GroupBy(e => e.Date).OrderByDescending(g => g.Key)
            .Select(g => (g.Key, g.Sum(e => e.DurationMinutes))).ToList();
}

public static class TimeFormat
{
    public static string Minutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var h = minutes / 60;
        var m = minutes % 60;
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>A variance: "+5h 30m" over, "-4h" under, "0m" on the nose.</summary>
    public static string Signed(int minutes) => minutes switch
    {
        > 0 => "+" + Minutes(minutes),
        < 0 => "-" + Minutes(-minutes),
        _ => "0m"
    };
}

/// <summary>
/// Outcome of stopping a running clock. <see cref="Entry"/> is null when too little time elapsed to log anything.
/// <see cref="PageLostAt"/> is set when the clock was stale: its run ended at its last heartbeat, given here (UTC).
/// </summary>
public sealed record ClockStopResult(TaskItem Task, TimeEntry? Entry, int Minutes, DateTime? PageLostAt = null);

/// <summary>
/// Outcome of starting a clock. <see cref="AlreadyRunning"/>: the caller's clock was already running on the task (it is
/// open in another tab) and was left as it was. <see cref="Previous"/>: a stale clock on the task was stopped first.
/// </summary>
public sealed record ClockStartResult(RunningClock Clock, ClockStopResult? Previous, bool AlreadyRunning);

/// <summary>Outcome of a page's clock heartbeat: whether the clock still runs, and the stop when it was found stale.</summary>
public sealed record ClockHeartbeatResult(bool Running, ClockStopResult? Stopped);

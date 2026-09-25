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
}

/// <summary>Outcome of stopping a running clock. <see cref="Entry"/> is null when too little time elapsed to log anything.</summary>
public sealed record ClockStopResult(TaskItem Task, TimeEntry? Entry, int Minutes);

/// <summary>Outcome of starting a clock. <see cref="Previous"/> is set when a clock already running elsewhere was stopped first.</summary>
public sealed record ClockStartResult(RunningClock Clock, ClockStopResult? Previous);

namespace Orbit.Data.Entities;

/// <summary>
/// A user's running "Start Clock" timer on a task (§6.10). At most one per user per task, so the clocks on different
/// tasks (a task and its subtask in another tab) run independently; stopping one turns its elapsed time into a
/// <see cref="TimeEntry"/> and deletes the row.
/// </summary>
public class RunningClock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The clock's page last checked in (its heartbeat). A clock not heard from for a while is stopped here.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of spec §6.15: what a dependency link means for statuses and planned dates.
/// <list type="bullet">
/// <item><b>Start</b> is leaving <c>Todo</c> (the moment <c>FirstRespondedAt</c> is set); <b>finish</b> is <c>Done</c>.</item>
/// <item>FS and SS gate the successor's start; FF and SF gate its finish.</item>
/// <item>A <c>Cancelled</c> predecessor constrains nothing: it counts as both started and finished.</item>
/// <item>Lag is a calendar notion: it feeds the date check only, never the gate.</item>
/// </list>
/// </summary>
public static class DependencyRules
{
    /// <summary>Whether a predecessor in this status has started, as far as its successors are concerned.</summary>
    public static bool HasStarted(TaskItemStatus predecessor) => predecessor != TaskItemStatus.Todo;

    /// <summary>Whether a predecessor in this status has finished, as far as its successors are concerned.</summary>
    public static bool HasFinished(TaskItemStatus predecessor) => predecessor.IsClosed();

    /// <summary>Whether the predecessor has done what this link waits for.</summary>
    public static bool IsMet(DependencyType type, TaskItemStatus predecessor) =>
        type.WaitsForFinish() ? HasFinished(predecessor) : HasStarted(predecessor);

    public static bool IsMet(TaskDependency link) => IsMet(link.Type, link.Predecessor.Status);

    /// <summary>A transition that starts the successor: out of Todo into real work (or straight to Done). Cancelling is never a start.</summary>
    public static bool IsStart(TaskItemStatus from, TaskItemStatus to) =>
        from == TaskItemStatus.Todo && to != TaskItemStatus.Todo && to != TaskItemStatus.Cancelled;

    /// <summary>A transition that finishes the successor.</summary>
    public static bool IsFinish(TaskItemStatus from, TaskItemStatus to) =>
        to == TaskItemStatus.Done && from != TaskItemStatus.Done;

    /// <summary>
    /// The planned-date check (a warning, never a rejection): the successor's gated end must not be before the
    /// predecessor's end plus the lag. Only meaningful when both dates are set.
    /// </summary>
    public static bool HasDateConflict(TaskDependency link) =>
        HasDateConflict(link.Type, link.LagDays, link.Predecessor, link.Successor);

    public static bool HasDateConflict(DependencyType type, int lagDays, TaskItem predecessor, TaskItem successor)
    {
        var predecessorEnd = type.WaitsForFinish() ? predecessor.DueDate : predecessor.StartDate;
        var successorEnd = type.GatesStart() ? successor.StartDate : successor.DueDate;
        if (predecessorEnd is null || successorEnd is null) return false;
        return successorEnd.Value < predecessorEnd.Value.AddDays(lagDays);
    }

    /// <summary>A task's own dates must be in order (§6.15).</summary>
    public static void RequireDatesInOrder(DateOnly? startDate, DateOnly? dueDate)
    {
        if (startDate is DateOnly s && dueDate is DateOnly d && s > d)
            throw new ValidationException("The start date can't be after the due date.");
    }
}

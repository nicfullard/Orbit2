using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>Time tracking rules (spec §6.10) that need no database.</summary>
public static class TimeRules
{
    /// <summary>
    /// Whether setting the task Done should first ask the actor to confirm: they are its assignee, may log their own time
    /// on it, and have neither logged any nor a clock running on it. A prompt in the web UI only, never a block (§13 item 63).
    /// </summary>
    public static bool AskBeforeDoneWithoutTime(Actor actor, TaskItem task, bool hasLoggedTime, bool clockRunning) =>
        actor.UserId is Guid me && task.AssigneeId == me && task.Status != TaskItemStatus.Done
        && AccessPolicy.CanLogTimeFor(actor, task, me) && !hasLoggedTime && !clockRunning;
}

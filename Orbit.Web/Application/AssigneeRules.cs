using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Who may be assigned to a task or a recurring definition, and what a change of assignees amounts to (spec §6.2.3). No
/// database access: the services load the people and say which of them see tasks in every department.
/// </summary>
public static class AssigneeRules
{
    /// <summary>The people a change adds and removes: the wanted set against the current one, each person once, in the order given.</summary>
    public static (IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed) Diff(IEnumerable<Guid> current, IEnumerable<Guid> wanted)
    {
        var before = current.Distinct().ToList();
        var after = wanted.Distinct().ToList();
        return (after.Except(before).ToList(), before.Except(after).ToList());
    }

    /// <summary>
    /// Why this person can't be assigned to work in the department, or null when they can: an assignee is an active person - never
    /// the Claude user - in that department, or one whose role sees tasks in every department (tasks.view at All, §6.5).
    /// </summary>
    public static string? Refusal(ApplicationUser user, Guid departmentId, bool seesEveryDepartment)
    {
        if (!user.IsActive || user.IsSystemAccount)
            return $"{user.DisplayName} can't be assigned: an assignee must be an active user.";
        if (user.DepartmentId != departmentId && !seesEveryDepartment)
            return $"{user.DisplayName} can't be assigned: a task can't be assigned to a user outside its own department.";
        return null;
    }

    /// <summary>
    /// Which of the task's assignees must qualify for a save to go through: the people it adds, and - only when the task is moving
    /// to another department - the people staying on it. Someone kept on a task that stays put isn't re-checked, as a kept
    /// requestee or asset isn't, so a colleague who has since been deactivated or changed department doesn't block every edit.
    /// </summary>
    public static IReadOnlyList<Guid> ToCheck(IEnumerable<Guid> current, IReadOnlyList<Guid> added, IReadOnlyList<Guid> removed, bool departmentChanging) =>
        departmentChanging ? current.Distinct().Except(removed).Concat(added).ToList() : added;

    /// <summary>
    /// Who is emailed that a task is now theirs (§6.7): each person added, except whoever made the change - nobody needs telling
    /// what they just did, and a change by the system or an API key leaves nobody out.
    /// </summary>
    public static IReadOnlyList<Guid> ToNotify(IEnumerable<Guid> added, Guid? actorUserId) =>
        added.Distinct().Where(id => id != actorUserId).ToList();
}

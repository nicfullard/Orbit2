using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Who may be a person's or a department's manager (spec §6.5, §6.6). No database access: the services load the person and the
/// reporting lines.
/// </summary>
public static class ManagerRules
{
    /// <summary>Why this person can't be made a manager, or null when they can: a manager is an active person, never the Claude user.</summary>
    public static string? Refusal(ApplicationUser manager) =>
        manager.IsActive && !manager.IsSystemAccount ? null : $"{manager.DisplayName} can't be a manager: a manager must be an active user.";

    /// <summary>
    /// Whether making <paramref name="managerId"/> the manager of <paramref name="userId"/> would have someone report to themselves:
    /// they are the same person, or the manager already reports to the user, directly or through others.
    /// <paramref name="managerOf"/> is every reporting line as it stands, each person to their manager.
    /// </summary>
    public static bool ReportsInACircle(Guid userId, Guid managerId, IReadOnlyDictionary<Guid, Guid> managerOf)
    {
        var seen = new HashSet<Guid>();
        for (Guid? at = managerId; at is Guid id; at = managerOf.TryGetValue(id, out var next) ? next : null)
        {
            if (id == userId) return true;
            // A circle that doesn't pass through this person is not this change's doing.
            if (!seen.Add(id)) return false;
        }
        return false;
    }
}

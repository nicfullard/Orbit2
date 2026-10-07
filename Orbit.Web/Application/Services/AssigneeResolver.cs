using Microsoft.EntityFrameworkCore;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Works out a change of assignees for a task or a recurring definition (spec §6.2.3): who is added and removed, with
/// <see cref="AssigneeRules"/> applied to the people it loads. Shared by <see cref="TaskService"/> and <see cref="RecurrenceService"/>.
/// </summary>
public static class AssigneeResolver
{
    /// <param name="Added">The people to add, checked and tracked, in the order they were named.</param>
    /// <param name="Removed">The ids of the people to remove.</param>
    public sealed record Change(IReadOnlyList<ApplicationUser> Added, IReadOnlyList<Guid> Removed)
    {
        public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
    }

    /// <param name="current">The assignees now.</param>
    /// <param name="wanted">The complete new set; null keeps the current one.</param>
    /// <param name="departmentId">The department the task will be in once saved.</param>
    /// <param name="departmentChanging">The task is moving to that department, so the people staying on it must qualify there too.</param>
    public static async Task<Change> ResolveAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> current, IReadOnlyCollection<Guid>? wanted,
        Guid departmentId, bool departmentChanging, CancellationToken ct)
    {
        var (added, removed) = AssigneeRules.Diff(current, wanted ?? current);
        var toCheck = AssigneeRules.ToCheck(current, added, removed, departmentChanging);
        if (toCheck.Count == 0) return new Change([], removed);

        // Tracked, so the assignment rows about to be added pick up their User.
        var users = await db.Users.Where(u => toCheck.Contains(u.Id)).ToListAsync(ct);
        if (users.Count != toCheck.Count) throw new NotFoundException("Assignee not found.");
        var everywhere = await RoleResolver.UserIdsWithScopeAllAsync(db, Permission.TasksView, ct);
        foreach (var user in users.OrderBy(u => u.DisplayName))
        {
            if (AssigneeRules.Refusal(user, departmentId, everywhere.Contains(user.Id)) is { } why)
                throw new ValidationException(why);
        }
        return new Change(added.Select(id => users.First(u => u.Id == id)).ToList(), removed);
    }
}

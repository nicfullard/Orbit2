using Orbit.Data.Entities;

namespace Orbit.Tests;

/// <summary>
/// The assignment rows a task or recurring-definition fixture carries (spec §6.2.3). The entities leave <c>Assignments</c> unset on
/// purpose, so that a task loaded without its assignees fails loudly instead of reading as unassigned; a fixture therefore says
/// who is assigned, or that nobody is.
/// </summary>
internal static class Assigned
{
    /// <summary>A task's assignments: one per id given, nulls skipped - so <c>To()</c> and <c>To(null)</c> are nobody.</summary>
    public static List<TaskAssignment> To(params Guid?[] users) =>
        users.OfType<Guid>().Select(id => new TaskAssignment { UserId = id, User = Person(id) }).ToList();

    /// <summary>A task's assignments to the people given, for fixtures that need their names or whether they are active.</summary>
    public static List<TaskAssignment> ToPeople(params ApplicationUser[] people) =>
        people.Select(u => new TaskAssignment { UserId = u.Id, User = u }).ToList();

    /// <summary>A recurring definition's assignments, as <see cref="To"/>.</summary>
    public static List<RecurringTaskAssignment> ToDefinition(params Guid?[] users) =>
        users.OfType<Guid>().Select(id => new RecurringTaskAssignment { UserId = id, User = Person(id) }).ToList();

    public static ApplicationUser Person(Guid id, string? name = null, Guid? department = null, bool active = true) =>
        new() { Id = id, DisplayName = name ?? id.ToString("N")[..8], DepartmentId = department, IsActive = active };
}

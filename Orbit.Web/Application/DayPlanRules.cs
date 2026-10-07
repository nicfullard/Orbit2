using System.Globalization;
using System.Text.Json;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// One person's part of a day plan (spec §6.12): the tasks on it they are an assignee of, with their estimated load.
/// <see cref="AssigneeId"/> null is the tasks nobody is assigned to.
/// </summary>
public sealed record DayPlanGroup(Guid? AssigneeId, string Name, IReadOnlyList<TaskItem> Tasks)
{
    public int Done => Tasks.Count(t => t.Status == TaskItemStatus.Done);
    /// <summary>Estimated minutes (§6.10) over every task on the plan, and over the ones still open - the load left in the day.</summary>
    public int EstimatedMinutes => Tasks.Sum(t => t.EstimateMinutes ?? 0);
    public int OpenEstimatedMinutes => Tasks.Where(t => t.IsOpen).Sum(t => t.EstimateMinutes ?? 0);
    /// <summary>Open tasks with no estimate, so the sum isn't read as the whole load.</summary>
    public int OpenUnestimated => Tasks.Count(t => t.IsOpen && t.EstimateMinutes is null);
    /// <summary>Tasks other people are assigned to as well (§6.2.3): each counts here in full, so the load is an upper bound.</summary>
    public int Shared => Tasks.Count(t => t.AssigneeIds.Count > 1);
}

/// <summary>
/// The day plan (spec §6.12) by person, and its carry-overs, read from a task's <c>Planned</c> audit rows:
/// <c>{"plannedFor":{"from":"2026-09-25","to":"2026-09-28"}}</c>, as <see cref="ChangeSet"/> writes it for the Today tick and
/// <c>TaskService.CarryOverAsync</c> writes it (with <c>carriedOver: true</c>) for the carry-over buttons.
/// </summary>
public static class DayPlanRules
{
    /// <summary>
    /// A day plan by person: one group per assignee, by name, then the tasks nobody is assigned to. A task several people share
    /// (§6.2.3) is in each of their groups with its whole estimate - there is no rule for splitting one. Tasks keep their order.
    /// </summary>
    public static IReadOnlyList<DayPlanGroup> GroupByAssignee(IEnumerable<TaskItem> planned)
    {
        var tasks = planned.ToList();
        var groups = tasks.SelectMany(t => t.Assignees.Select(u => (User: u, Task: t)))
            .GroupBy(x => x.User.Id)
            .Select(g => new DayPlanGroup(g.Key, g.First().User.DisplayName, g.Select(x => x.Task).ToList()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.AssigneeId)
            .ToList();
        var unassigned = tasks.Where(t => t.IsUnassigned).ToList();
        if (unassigned.Count > 0) groups.Add(new DayPlanGroup(null, "Unassigned", unassigned));
        return groups;
    }

    /// <summary>From this many carry-overs a task's badge is shown in red: it keeps slipping.</summary>
    public const int RepeatedCarryOvers = 3;

    /// <summary>
    /// True when the entry moved the task from one day's plan to a later day's. A first planning (no <c>from</c>, which the
    /// serializer omits when null), a move to an earlier or the same day, any other change and bad JSON are not carry-overs.
    /// </summary>
    public static bool IsCarryOver(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return false;
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("plannedFor", out var planned)
                && planned.ValueKind == JsonValueKind.Object
                && TryReadDate(planned, "from", out var from)
                && TryReadDate(planned, "to", out var to)
                && from < to;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The number of carry-overs per task in <paramref name="plannedEntries"/>; tasks never carried over are absent.</summary>
    public static IReadOnlyDictionary<Guid, int> CountCarryOvers(IEnumerable<(Guid TaskId, string? Details)> plannedEntries) =>
        plannedEntries.Where(e => IsCarryOver(e.Details))
            .GroupBy(e => e.TaskId)
            .ToDictionary(g => g.Key, g => g.Count());

    private static bool TryReadDate(JsonElement parent, string name, out DateOnly date)
    {
        date = default;
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}

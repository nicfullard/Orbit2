using System.Globalization;
using System.Text.Json;

namespace Orbit.Application;

/// <summary>
/// Carry-overs on the day plan (spec §6.12), read from a task's <c>Planned</c> audit rows:
/// <c>{"plannedFor":{"from":"2026-09-25","to":"2026-09-28"}}</c>, as <see cref="ChangeSet"/> writes it for the Today tick and
/// <c>TaskService.CarryOverAsync</c> writes it (with <c>carriedOver: true</c>) for the carry-over buttons.
/// </summary>
public static class DayPlanRules
{
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

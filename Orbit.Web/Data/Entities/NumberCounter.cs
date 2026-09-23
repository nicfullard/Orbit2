namespace Orbit.Data.Entities;

/// <summary>
/// The last human-readable number handed out for one prefix in one year (spec §5.1): the "00012" of T-26-00012.
/// One row per prefix and year, bumped atomically by <c>NumberingService</c>; gaps are allowed (a create that fails
/// after taking a number simply skips it).
/// </summary>
public class NumberCounter
{
    /// <summary>"T" for tasks, "P" for projects.</summary>
    public string Prefix { get; set; } = string.Empty;
    /// <summary>The four-digit year of creation the sequence belongs to.</summary>
    public int Year { get; set; }
    /// <summary>The last number used; the next create gets Last + 1.</summary>
    public int Last { get; set; }
}

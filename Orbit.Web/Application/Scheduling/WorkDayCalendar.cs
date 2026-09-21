namespace Orbit.Application.Scheduling;

/// <summary>A dated exception to the working week: a non-working holiday or shutdown day, or an exceptional working day.</summary>
public sealed record CalendarDay(DateOnly Date, bool IsWorking, string Name);

/// <summary>
/// The organisation's working days as arithmetic (spec §6.17): a working week plus dated exceptions (public holidays,
/// shutdown days, exceptional working days). Every working day has an <em>ordinal</em> - its position in the sequence
/// of working days since a fixed epoch - so that "five working days before the target" or "how many working days of
/// float" is a subtraction. Pure and deterministic; one instance per analysis.
/// </summary>
public sealed class WorkDayCalendar
{
    private static readonly DateOnly Epoch = new(1990, 1, 1);
    private readonly bool[] _week = new bool[7];
    private readonly SortedDictionary<DateOnly, CalendarDay> _exceptions = new();
    /// <summary>_prefix[i] = number of working days in [Epoch, Epoch + i days].</summary>
    private readonly List<int> _prefix = [];
    /// <summary>_byOrdinal[k] = the working day whose ordinal is k + 1.</summary>
    private readonly List<DateOnly> _byOrdinal = [];

    public WorkDayCalendar(IEnumerable<DayOfWeek> workingDays, IEnumerable<CalendarDay>? exceptions = null)
    {
        foreach (var day in workingDays) _week[(int)day] = true;
        if (!_week.Any(w => w)) throw new ArgumentException("At least one weekday must be a working day.", nameof(workingDays));
        if (exceptions is not null)
            foreach (var x in exceptions) _exceptions[x.Date] = x;
    }

    /// <summary>Monday to Friday, no exceptions.</summary>
    public static WorkDayCalendar Default => new([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);

    public IReadOnlyList<DayOfWeek> WorkingWeek => Enum.GetValues<DayOfWeek>().Where(d => _week[(int)d]).ToList();
    public IEnumerable<CalendarDay> Exceptions => _exceptions.Values;

    public bool IsWorking(DateOnly d) => _exceptions.TryGetValue(d, out var x) ? x.IsWorking : _week[(int)d.DayOfWeek];

    /// <summary>The exception on this date, if any.</summary>
    public CalendarDay? Exception(DateOnly d) => _exceptions.TryGetValue(d, out var x) ? x : null;

    /// <summary>Exceptions dated within [<paramref name="from"/>, <paramref name="to"/>], in date order.</summary>
    public IEnumerable<CalendarDay> ExceptionsBetween(DateOnly from, DateOnly to) =>
        _exceptions.Values.Where(x => x.Date >= from && x.Date <= to);

    /// <summary>The first working day on or after <paramref name="d"/>.</summary>
    public DateOnly Ceil(DateOnly d)
    {
        while (!IsWorking(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>The last working day on or before <paramref name="d"/>.</summary>
    public DateOnly Floor(DateOnly d)
    {
        while (!IsWorking(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>
    /// The position of a working day in the sequence of working days (1 = the first working day of 1990). For a
    /// non-working day this is the ordinal of the last working day before it, i.e. <c>Ordinal(Floor(d))</c>.
    /// </summary>
    public int Ordinal(DateOnly d)
    {
        EnsureThrough(d);
        return _prefix[d.DayNumber - Epoch.DayNumber];
    }

    /// <summary>The working day with this ordinal - the inverse of <see cref="Ordinal"/>.</summary>
    public DateOnly Date(int ordinal)
    {
        if (ordinal < 1) throw new ArgumentOutOfRangeException(nameof(ordinal), "Ordinals start at 1.");
        while (_byOrdinal.Count < ordinal) EnsureThrough(Epoch.AddDays(_prefix.Count + 60));
        return _byOrdinal[ordinal - 1];
    }

    /// <summary>
    /// <paramref name="n"/> working days after (negative: before) <paramref name="d"/>, counted from the last working
    /// day on or before <paramref name="d"/>. Zero returns that working day itself.
    /// </summary>
    public DateOnly AddWorkingDays(DateOnly d, int n) => Date(Ordinal(Floor(d)) + n);

    /// <summary>Working days in [<paramref name="from"/>, <paramref name="to"/>], inclusive; 0 when the range is empty.</summary>
    public int CountWorkingDays(DateOnly from, DateOnly to) =>
        to < from ? 0 : Ordinal(to) - Ordinal(from) + (IsWorking(from) ? 1 : 0);

    private void EnsureThrough(DateOnly d)
    {
        var index = d.DayNumber - Epoch.DayNumber;
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(d), $"Dates before {Epoch:yyyy-MM-dd} aren't supported.");
        while (_prefix.Count <= index)
        {
            var day = Epoch.AddDays(_prefix.Count);
            var count = _prefix.Count == 0 ? 0 : _prefix[^1];
            if (IsWorking(day))
            {
                count++;
                _byOrdinal.Add(day);
            }
            _prefix.Add(count);
        }
    }
}

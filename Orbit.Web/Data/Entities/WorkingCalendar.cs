namespace Orbit.Data.Entities;

/// <summary>
/// The organisation's working week (spec §6.17): a single row (<see cref="WellKnownIds.WorkingCalendarId"/>) saying
/// which weekdays are working days, edited under Admin &gt; Working Calendar. Dated exceptions (public holidays,
/// shutdown days, exceptional working days) are <see cref="WorkingCalendarException"/> rows. Critical path analysis
/// counts in it and the Gantt shades its non-working days; nothing in Orbit moves a date because of it.
/// </summary>
public class WorkingCalendar
{
    public Guid Id { get; set; } = WellKnownIds.WorkingCalendarId;
    public bool MondayWorking { get; set; } = true;
    public bool TuesdayWorking { get; set; } = true;
    public bool WednesdayWorking { get; set; } = true;
    public bool ThursdayWorking { get; set; } = true;
    public bool FridayWorking { get; set; } = true;
    public bool SaturdayWorking { get; set; }
    public bool SundayWorking { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }

    public bool IsWorking(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => MondayWorking,
        DayOfWeek.Tuesday => TuesdayWorking,
        DayOfWeek.Wednesday => WednesdayWorking,
        DayOfWeek.Thursday => ThursdayWorking,
        DayOfWeek.Friday => FridayWorking,
        DayOfWeek.Saturday => SaturdayWorking,
        _ => SundayWorking
    };

    public IReadOnlySet<DayOfWeek> WorkingDays =>
        Enum.GetValues<DayOfWeek>().Where(IsWorking).ToHashSet();
}

/// <summary>A dated exception to the working week (spec §6.17): a non-working public holiday or shutdown day, or an exceptional working day.</summary>
public class WorkingCalendarException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>False for a holiday or shutdown day; true for a day that is worked although the week says otherwise.</summary>
    public bool IsWorking { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
}

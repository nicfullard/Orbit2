using Orbit.Application.Scheduling;
using static Orbit.Tests.Scheduling.Fixture;

namespace Orbit.Tests.Scheduling;

public class WorkDayCalendarTests
{
    [Fact]
    public void Default_week_is_monday_to_friday()
    {
        var cal = WorkDayCalendar.Default;
        Assert.True(cal.IsWorking(D("2026-10-05")));  // Mon
        Assert.True(cal.IsWorking(D("2026-10-09")));  // Fri
        Assert.False(cal.IsWorking(D("2026-10-10"))); // Sat
        Assert.False(cal.IsWorking(D("2026-10-11"))); // Sun
    }

    [Fact]
    public void Ceil_and_floor_skip_the_weekend()
    {
        var cal = WorkDayCalendar.Default;
        Assert.Equal(D("2026-10-12"), cal.Ceil(D("2026-10-10")));
        Assert.Equal(D("2026-10-09"), cal.Floor(D("2026-10-11")));
        Assert.Equal(D("2026-10-07"), cal.Ceil(D("2026-10-07")));
    }

    [Fact]
    public void Ordinal_and_date_are_inverses_on_working_days()
    {
        var cal = WorkDayCalendar.Default;
        for (var d = D("2026-09-28"); d <= D("2026-11-06"); d = d.AddDays(1))
        {
            if (!cal.IsWorking(d)) continue;
            Assert.Equal(d, cal.Date(cal.Ordinal(d)));
        }
        // Fri 9 -> Mon 12 is one working day apart.
        Assert.Equal(1, cal.Ordinal(D("2026-10-12")) - cal.Ordinal(D("2026-10-09")));
        // A weekend day carries the ordinal of the Friday before it.
        Assert.Equal(cal.Ordinal(D("2026-10-09")), cal.Ordinal(D("2026-10-10")));
    }

    [Fact]
    public void AddWorkingDays_counts_working_days_only()
    {
        var cal = WorkDayCalendar.Default;
        Assert.Equal(D("2026-10-12"), cal.AddWorkingDays(D("2026-10-09"), 1));
        Assert.Equal(D("2026-10-09"), cal.AddWorkingDays(D("2026-10-10"), 0));
        Assert.Equal(D("2026-10-23"), cal.AddWorkingDays(D("2026-10-30"), -5));
        Assert.Equal(D("2026-10-23"), cal.AddWorkingDays(D("2026-10-31"), -5)); // Saturday target: counted from Friday
    }

    [Fact]
    public void CountWorkingDays_is_inclusive()
    {
        var cal = WorkDayCalendar.Default;
        Assert.Equal(5, cal.CountWorkingDays(D("2026-10-05"), D("2026-10-09")));
        Assert.Equal(2, cal.CountWorkingDays(D("2026-10-09"), D("2026-10-12")));
        Assert.Equal(0, cal.CountWorkingDays(D("2026-10-10"), D("2026-10-11")));
        Assert.Equal(0, cal.CountWorkingDays(D("2026-10-09"), D("2026-10-05")));
    }

    [Fact]
    public void Exceptions_override_the_week_in_both_directions()
    {
        var cal = new WorkDayCalendar(WorkDayCalendar.Default.WorkingWeek,
        [
            new CalendarDay(D("2026-10-07"), false, "Test Day"),
            new CalendarDay(D("2026-10-10"), true, "Stocktake Saturday")
        ]);
        Assert.False(cal.IsWorking(D("2026-10-07")));
        Assert.True(cal.IsWorking(D("2026-10-10")));
        Assert.Equal(D("2026-10-08"), cal.Ceil(D("2026-10-07")));
        Assert.Equal(5, cal.CountWorkingDays(D("2026-10-05"), D("2026-10-10"))); // Mon, Tue, Thu, Fri, Sat
        Assert.Equal("Test Day", cal.Exception(D("2026-10-07"))!.Name);
        Assert.Single(cal.ExceptionsBetween(D("2026-10-05"), D("2026-10-09")));
    }

    [Fact]
    public void A_week_with_no_working_day_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkDayCalendar([]));
    }
}

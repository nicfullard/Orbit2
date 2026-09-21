using Orbit.Application.Scheduling;
using Orbit.Data.Entities;
using static Orbit.Tests.Scheduling.Fixture;

namespace Orbit.Tests.Scheduling;

/// <summary>Staleness (spec §6.17, CPA-016 ... CPA-018): only schedule-driving changes alter the fingerprint.</summary>
public class ScheduleFingerprintTests
{
    private static string Print(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency> links, string? target = "2026-10-30", int? buffer = 5, WorkDayCalendar? cal = null) =>
        ScheduleFingerprint.Compute(tasks, links, target is null ? null : D(target), buffer, cal ?? WorkDayCalendar.Default);

    [Fact] // CPA-016
    public void Date_and_dependency_changes_alter_it()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var link = Link(a, b);
        var before = Print([a, b], [link]);

        b.DueDate = D("2026-10-12");
        Assert.NotEqual(before, Print([a, b], [link]));
        b.DueDate = D("2026-10-09");
        Assert.Equal(before, Print([a, b], [link]));

        link.LagDays = 1;
        Assert.NotEqual(before, Print([a, b], [link]));
        link.LagDays = 0;
        link.Type = DependencyType.StartToStart;
        Assert.NotEqual(before, Print([a, b], [link]));
        link.Type = DependencyType.FinishToStart;

        Assert.NotEqual(before, Print([a, b], []));
        Assert.NotEqual(before, Print([a, b, Task("C", "2026-10-12", "2026-10-13")], [link]));
        Assert.NotEqual(before, Print([a, b], [link], target: "2026-11-06"));
        Assert.NotEqual(before, Print([a, b], [link], buffer: 3));
    }

    [Fact] // CPA-017 and CPA-018
    public void Non_scheduling_changes_do_not_alter_it()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var link = Link(a, b);
        var before = Print([a, b], [link]);

        a.EstimateMinutes = 480;
        a.Status = TaskItemStatus.InProgress;
        a.Title = "Renamed";
        a.Description = "A comment or description changes nothing here";
        b.Status = TaskItemStatus.Done;
        Assert.Equal(before, Print([a, b], [link]));

        // An undated task joining the project doesn't change the schedule either.
        Assert.Equal(before, Print([a, b, Task("Undated", null, null)], [link]));
    }

    [Fact]
    public void Cancelling_a_scheduled_task_alters_it()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var before = Print([a, b], [Link(a, b)]);
        b.Status = TaskItemStatus.Cancelled;
        Assert.NotEqual(before, Print([a, b], [Link(a, b)]));
    }

    [Fact]
    public void Calendar_changes_near_the_project_alter_it_and_far_away_ones_do_not()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var link = Link(a, b);
        var before = Print([a, b], [link]);

        var week = WorkDayCalendar.Default.WorkingWeek;
        var nearby = new WorkDayCalendar(week, [new CalendarDay(D("2026-10-07"), false, "Holiday")]);
        Assert.NotEqual(before, Print([a, b], [link], cal: nearby));

        var farAway = new WorkDayCalendar(week, [new CalendarDay(D("2028-12-25"), false, "Christmas 2028")]);
        Assert.Equal(before, Print([a, b], [link], cal: farAway));

        var fourDayWeek = new WorkDayCalendar([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday]);
        Assert.NotEqual(before, Print([a, b], [link], cal: fourDayWeek));
    }
}

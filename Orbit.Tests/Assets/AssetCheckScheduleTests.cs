using Orbit.Application.Assets;
using Orbit.Data.Entities;

namespace Orbit.Tests.Assets;

/// <summary>The check schedule and the last check (spec §6.19).</summary>
public class AssetCheckScheduleTests
{
    private static readonly DateOnly Registered = new(2026, 1, 10);
    private static readonly DateOnly Today = new(2026, 9, 24);

    /// <summary>AST-006: one interval after the last check, or after registration when never checked; none without an interval or when Lost/Disposed.</summary>
    [Fact]
    public void Next_check_due_follows_the_last_check_or_registration()
    {
        Assert.Equal(new DateOnly(2026, 7, 1), AssetCheckSchedule.NextDue(AssetStatus.Active, 30, new DateOnly(2026, 6, 1), Registered));
        Assert.Equal(Registered.AddDays(365), AssetCheckSchedule.NextDue(AssetStatus.InStorage, 365, null, Registered));
        Assert.Equal(Registered.AddDays(90), AssetCheckSchedule.NextDue(AssetStatus.Damaged, 90, null, Registered));
        Assert.Null(AssetCheckSchedule.NextDue(AssetStatus.Active, null, null, Registered));
        Assert.Null(AssetCheckSchedule.NextDue(AssetStatus.Lost, 30, null, Registered));
        Assert.Null(AssetCheckSchedule.NextDue(AssetStatus.Disposed, 30, new DateOnly(2026, 6, 1), Registered));
    }

    /// <summary>AST-007: due today is due soon, not overdue; due yesterday is overdue.</summary>
    [Fact]
    public void Overdue_and_due_soon_boundaries()
    {
        Assert.Equal(CheckDueState.DueSoon, AssetCheckSchedule.StateOf(Today, Today, 14));
        Assert.Equal(CheckDueState.Overdue, AssetCheckSchedule.StateOf(Today.AddDays(-1), Today, 14));
        Assert.Equal(CheckDueState.DueSoon, AssetCheckSchedule.StateOf(Today.AddDays(14), Today, 14));
        Assert.Equal(CheckDueState.Ok, AssetCheckSchedule.StateOf(Today.AddDays(15), Today, 14));
        Assert.Equal(CheckDueState.None, AssetCheckSchedule.StateOf(null, Today, 14));
    }

    /// <summary>AST-008: the latest check by date sets the last check; a back-dated check doesn't displace a later one; ties go to the later record.</summary>
    [Fact]
    public void The_latest_check_by_date_is_the_last_check()
    {
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var june = new AssetCheck { CheckDate = new DateOnly(2026, 6, 1), Outcome = AssetCheckOutcome.Ok, CreatedAt = t0 };
        var backDated = new AssetCheck { CheckDate = new DateOnly(2026, 3, 1), Outcome = AssetCheckOutcome.NotFound, CreatedAt = t0.AddDays(1) };
        Assert.Same(june, AssetCheckSchedule.Latest([june, backDated]));

        var sameDayLater = new AssetCheck { CheckDate = june.CheckDate, Outcome = AssetCheckOutcome.IssueFound, CreatedAt = t0.AddHours(2) };
        Assert.Same(sameDayLater, AssetCheckSchedule.Latest([june, backDated, sameDayLater]));
        Assert.Null(AssetCheckSchedule.Latest([]));

        Assert.True(AssetCheckSchedule.LastCheckNotOk(new Asset { LastCheckOutcome = AssetCheckOutcome.NotFound }));
        Assert.False(AssetCheckSchedule.LastCheckNotOk(new Asset { LastCheckOutcome = AssetCheckOutcome.Ok }));
        Assert.False(AssetCheckSchedule.LastCheckNotOk(new Asset()));
    }
}

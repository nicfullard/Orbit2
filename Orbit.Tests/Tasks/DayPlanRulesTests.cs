using System.Text.Json;
using Orbit.Application;

namespace Orbit.Tests.Tasks;

/// <summary>Carry-overs on the day plan (spec §6.12).</summary>
public class DayPlanRulesTests
{
    private static readonly DateOnly Friday = new(2026, 9, 25);
    private static readonly DateOnly Saturday = new(2026, 9, 26);
    private static readonly DateOnly Monday = new(2026, 9, 28);

    /// <summary>What <c>TaskService.SetPlannedForAsync</c> writes: the Today tick, the task page's button and MCP <c>update_task</c>.</summary>
    private static string Tick(DateOnly? from, DateOnly? to) =>
        JsonSerializer.Serialize(new ChangeSet().Track("plannedFor", from, to).Changes, OrbitJson.Options);

    /// <summary>What <c>TaskService.CarryOverAsync</c> writes: "Carry selected over" and "Carry all over".</summary>
    private static string Button(DateOnly? from, DateOnly to) =>
        JsonSerializer.Serialize(new { plannedFor = new { from, to }, carriedOver = true }, OrbitJson.Options);

    /// <summary>DAY-001: a move to a later day's plan is a carry-over, whether made with the carry-over buttons or by ticking Today.</summary>
    [Fact]
    public void A_move_to_a_later_day_is_a_carry_over()
    {
        Assert.True(DayPlanRules.IsCarryOver(Button(Friday, Monday)));
        Assert.True(DayPlanRules.IsCarryOver(Tick(Friday, Saturday)));
    }

    /// <summary>DAY-002: a first planning, a move to an earlier or the same day, taking a task off the plan and any other entry are not.</summary>
    [Fact]
    public void Other_plan_changes_are_not_carry_overs()
    {
        Assert.False(DayPlanRules.IsCarryOver(Tick(null, Monday)));      // first planning: the null "from" is omitted
        Assert.False(DayPlanRules.IsCarryOver(Tick(Monday, Friday)));    // moved to an earlier day
        Assert.False(DayPlanRules.IsCarryOver(Button(Monday, Monday)));  // the service skips this, but it isn't a carry-over either
        Assert.False(DayPlanRules.IsCarryOver(Tick(Friday, null)));      // taken off the plan
        Assert.False(DayPlanRules.IsCarryOver(JsonSerializer.Serialize(new ChangeSet().Track("dueDate", Friday, Monday).Changes, OrbitJson.Options)));
        Assert.False(DayPlanRules.IsCarryOver("""{"plannedFor":{"from":"25/09/2026","to":"28/09/2026"}}"""));
        Assert.False(DayPlanRules.IsCarryOver("{}"));
        Assert.False(DayPlanRules.IsCarryOver("not json"));
        Assert.False(DayPlanRules.IsCarryOver(null));
    }

    /// <summary>DAY-003: carry-overs are counted per task, over the task's whole history; a task never carried over is absent.</summary>
    [Fact]
    public void Counts_carry_overs_per_task()
    {
        var slipping = Guid.NewGuid();
        var once = Guid.NewGuid();
        var never = Guid.NewGuid();

        var counts = DayPlanRules.CountCarryOvers(
        [
            (slipping, Tick(null, Friday)),
            (slipping, Button(Friday, Monday)),
            (slipping, Tick(Monday, new DateOnly(2026, 9, 29))),
            (slipping, Tick(null, new DateOnly(2026, 10, 5))),   // taken off in between and planned again: not a carry-over
            (slipping, Button(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6))),
            (once, Tick(null, Friday)),
            (once, Button(Friday, Monday)),
            (never, Tick(null, Monday)),
        ]);

        Assert.Equal(3, counts[slipping]);
        Assert.Equal(1, counts[once]);
        Assert.False(counts.ContainsKey(never));
    }
}

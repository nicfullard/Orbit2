using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data.Entities;
using static Orbit.Tests.Scheduling.Fixture;

namespace Orbit.Tests.Scheduling;

/// <summary>The acceptance cases of spec §6.17 (CPA-001 ... CPA-015) plus the conventions the engine documents.</summary>
public class CriticalPathEngineTests
{
    [Fact] // CPA-001
    public void Fs_chain_is_critical_end_to_end()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-08");
        var c = Task("C", "2026-10-08", "2026-10-09");
        var r = Run([a, b, c], [Link(a, b), Link(b, c)]);

        Assert.False(r.Blocked);
        Assert.All(new[] { a, b, c }, t => Assert.True(r.Row(t).IsCritical));
        Assert.All(new[] { a, b, c }, t => Assert.Equal(0, r.Row(t).TotalFloat));
        var path = Assert.Single(r.CriticalPaths);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, path.TaskIds);
        Assert.Equal(D("2026-10-09"), r.Schedule.PlannedCompletion);
        Assert.Equal(D("2026-10-09"), r.Schedule.NetworkCompletion);
        Assert.Equal(2, r.DrivingLinkIds.Count);
    }

    [Fact] // the §6.15 day rule: a successor planned the day AFTER its predecessor's due day leaves the predecessor a day of float
    public void Fs_successor_starting_next_day_gives_predecessor_one_day_float()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-07", "2026-10-08");
        var r = Run([a, b], [Link(a, b)]);

        Assert.Equal(1, r.Row(a).TotalFloat);
        Assert.Equal(1, r.Row(a).FreeFloat);
        Assert.False(r.Row(a).IsCritical);
        Assert.True(r.Row(b).IsCritical);
    }

    [Fact] // same-day FS is not a conflict and not negative float
    public void Fs_successor_starting_on_due_day_is_tight_not_negative()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-08");
        var r = Run([a, b], [Link(a, b)]);

        Assert.Equal(0, r.Row(a).TotalFloat);
        Assert.Equal(0, r.Row(b).TotalFloat);
        Assert.Null(r.Warning(PlanIssueCodes.DateConflict));
    }

    [Fact] // CPA-002
    public void Ss_link_constrains_the_successor_start()
    {
        var a = Task("A", "2026-10-05", "2026-10-09");
        var b = Task("B", "2026-10-02", "2026-10-07"); // planned before A starts: pushed to Mon 5
        var r = Run([a, b], [Link(a, b, DependencyType.StartToStart)]);

        Assert.Equal(D("2026-10-05"), r.Row(b).EarlyStart);
        Assert.Equal(D("2026-10-08"), r.Row(b).EarlyFinish); // 4 working days (2, 5, 6, 7) from Mon 5
        Assert.True(r.Row(a).IsCritical);
        Assert.NotNull(r.Warning(PlanIssueCodes.DateConflict)); // CPA-007: the existing date check flags it
    }

    [Fact] // CPA-003
    public void Ff_link_constrains_the_successor_finish()
    {
        var a = Task("A", "2026-10-05", "2026-10-07");
        var b = Task("B", "2026-10-05", "2026-10-06");
        var r = Run([a, b], [Link(a, b, DependencyType.FinishToFinish)]);

        Assert.Equal(D("2026-10-07"), r.Row(b).EarlyFinish);
        Assert.Equal(D("2026-10-06"), r.Row(b).EarlyStart);
        Assert.True(r.Row(a).IsCritical);
        Assert.True(r.Row(b).IsCritical);
        Assert.Single(r.DrivingLinkIds);
    }

    [Fact] // CPA-004
    public void Sf_link_constrains_the_successor_finish_by_the_predecessor_start()
    {
        var a = Task("A", "2026-10-08", "2026-10-09");
        var b = Task("B", "2026-10-05", "2026-10-06");
        var r = Run([a, b], [Link(a, b, DependencyType.StartToFinish)]);

        Assert.Equal(D("2026-10-08"), r.Row(b).EarlyFinish);
        Assert.Equal(D("2026-10-07"), r.Row(b).EarlyStart);
        Assert.True(r.Row(a).IsCritical);
        Assert.Equal(1, r.Row(b).TotalFloat);
    }

    [Fact] // CPA-005 and CPA-009
    public void Lag_is_calendar_days_rolled_to_the_next_working_day()
    {
        var a = Task("A", "2026-10-05", "2026-10-09"); // due Fri
        var b = Task("B", "2026-10-12", "2026-10-13");
        var r = Run([a, b], [Link(a, b, lag: 1)]); // Fri + 1 = Sat -> Mon

        Assert.Equal(D("2026-10-12"), r.Row(b).EarlyStart);
        Assert.True(r.Row(a).IsCritical);
        Assert.True(r.Row(b).IsCritical);
        Assert.Equal(5, r.Row(a).SpanWorkingDays);
    }

    [Fact] // a lag that lands on a weekend gives the predecessor real slack
    public void Lag_across_a_weekend_can_leave_float()
    {
        var a = Task("A", "2026-10-05", "2026-10-08"); // due Thu
        var b = Task("B", "2026-10-12", "2026-10-13");
        var r = Run([a, b], [Link(a, b, lag: 2)]); // Thu + 2 = Sat -> Mon; A could finish Fri and B would still start Mon

        Assert.Equal(D("2026-10-12"), r.Row(b).EarlyStart);
        Assert.Equal(1, r.Row(a).TotalFloat);
    }

    [Fact] // CPA-006
    public void Circular_dependency_blocks_and_names_the_loop()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-07");
        var c = Task("C", "2026-10-07", "2026-10-08");
        var d = Task("D", "2026-10-08", "2026-10-09"); // downstream of the loop, not in it
        var r = Run([a, b, c, d], [Link(a, b), Link(b, c), Link(c, a), Link(c, d)]);

        Assert.True(r.Blocked);
        var error = Assert.Single(r.Errors);
        Assert.Equal(PlanIssueCodes.CircularDependency, error.Code);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }.Order(), error.TaskIds.Order());
        Assert.Empty(r.Tasks);
    }

    [Fact] // CPA-007
    public void Existing_date_conflict_is_a_readiness_warning()
    {
        var a = Task("A", "2026-10-05", "2026-10-07");
        var b = Task("B", "2026-10-06", "2026-10-08");
        var link = Link(a, b);
        var r = Run([a, b], [link]);

        var w = r.Warning(PlanIssueCodes.DateConflict);
        Assert.NotNull(w);
        Assert.Contains(link.Id, w.LinkIds);
        Assert.Equal(D("2026-10-07"), r.Row(b).EarlyStart); // and the analysis places B where the link allows
    }

    [Fact] // CPA-008
    public void Estimate_is_effort_not_duration()
    {
        var a = Task("A", "2026-10-05", "2026-10-07", estimateMinutes: 240);
        var b = Task("B", "2026-10-07", "2026-10-08");
        var r = Run([a, b], [Link(a, b)]);

        Assert.Equal(3, r.Row(a).SpanWorkingDays);          // Mon-Wed, not 4 hours and not 3 days of effort
        Assert.Equal(D("2026-10-07"), r.Row(a).EarlyFinish);
        var opportunity = Assert.Single(r.Opportunities);
        Assert.Equal(1, opportunity.EstimateWorkingDays);
        Assert.Equal(2, opportunity.PotentialDays);
    }

    [Fact] // CPA-010
    public void Holiday_is_excluded_from_working_days_and_surfaced()
    {
        var cal = new WorkDayCalendar(WorkDayCalendar.Default.WorkingWeek, [new CalendarDay(D("2026-10-07"), false, "Heritage Day")]);
        var a = Task("A", "2026-10-05", "2026-10-09");
        var b = Task("B", "2026-10-09", "2026-10-12");
        var r = Run([a, b], [Link(a, b)], calendar: cal);

        Assert.Equal(4, r.Row(a).SpanWorkingDays);
        var w = r.Warning(PlanIssueCodes.HolidayInWindow);
        Assert.NotNull(w);
        Assert.Contains("Heritage Day", w.Message);
        Assert.Contains(a.Id, w.TaskIds);
    }

    [Fact] // CPA-011
    public void Missing_target_date_still_gives_a_critical_path_but_no_buffer()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var r = Run([a, b], [Link(a, b)], buffer: 5);

        Assert.Single(r.CriticalPaths);
        Assert.Equal(BufferStatus.NotAvailable, r.Schedule.BufferStatus);
        Assert.Null(r.Schedule.InternalCompletion);
        Assert.NotNull(r.Warning(PlanIssueCodes.NoTargetDate));
    }

    [Fact] // CPA-012
    public void Buffer_intact_with_headroom()
    {
        var a = Task("A", "2026-10-05", "2026-10-16");
        var b = Task("B", "2026-10-16", "2026-10-21"); // Wed 21 Oct
        var r = Run([a, b], [Link(a, b)], target: "2026-10-30", buffer: 5); // Fri 30 -> internal Fri 23

        Assert.Equal(D("2026-10-23"), r.Schedule.InternalCompletion);
        Assert.Equal(0, r.Schedule.BufferConsumedDays);
        Assert.Equal(5, r.Schedule.BufferRemainingDays);
        Assert.Equal(2, r.Schedule.HeadroomDays); // Thu 22, Fri 23
        Assert.Equal(0, r.Schedule.BufferConsumptionPercent);
        Assert.Equal(BufferStatus.Green, r.Schedule.BufferStatus);
    }

    [Fact] // CPA-013
    public void Buffer_consumption_is_measured_in_working_days_and_zoned()
    {
        var a = Task("A", "2026-10-05", "2026-10-16");
        var b = Task("B", "2026-10-16", "2026-10-27"); // Tue 27 Oct: Mon 26 and Tue 27 are inside the buffer
        var r = Run([a, b], [Link(a, b)], target: "2026-10-30", buffer: 5);

        Assert.Equal(2, r.Schedule.BufferConsumedDays);
        Assert.Equal(3, r.Schedule.BufferRemainingDays);
        Assert.Equal(40, r.Schedule.BufferConsumptionPercent);
        Assert.Equal(BufferStatus.Amber, r.Schedule.BufferStatus);
        Assert.Equal(0, r.Schedule.DaysBeyondTarget);
    }

    [Fact] // CPA-014
    public void Completion_beyond_target_is_red_with_negative_remaining_buffer()
    {
        var a = Task("A", "2026-10-05", "2026-10-16");
        var b = Task("B", "2026-10-16", "2026-11-03"); // Tue 3 Nov
        var r = Run([a, b], [Link(a, b)], target: "2026-10-30", buffer: 5);

        Assert.Equal(BufferStatus.Red, r.Schedule.BufferStatus);
        Assert.Equal(2, r.Schedule.DaysBeyondTarget); // Mon 2, Tue 3
        Assert.Equal(-2, r.Schedule.BufferRemainingDays);
        Assert.Equal(7, r.Schedule.BufferConsumedDays);
    }

    [Fact] // a Saturday target is measured from the Friday before it
    public void Target_on_a_non_working_day_uses_the_previous_working_day()
    {
        var a = Task("A", "2026-10-05", "2026-10-16");
        var b = Task("B", "2026-10-16", "2026-10-21");
        var r = Run([a, b], [Link(a, b)], target: "2026-10-31", buffer: 5);

        Assert.Equal(D("2026-10-23"), r.Schedule.InternalCompletion);
        Assert.NotNull(r.Warning(PlanIssueCodes.TargetNonWorkingDay));
    }

    [Fact] // CPA-015
    public void Two_independent_critical_paths_are_both_returned()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var c = Task("C", "2026-10-06", "2026-10-09");
        var b = Task("B", "2026-10-05", "2026-10-06");
        var d = Task("D", "2026-10-06", "2026-10-09");
        var r = Run([a, b, c, d], [Link(a, c), Link(b, d)]);

        Assert.Equal(2, r.CriticalPaths.Count);
        Assert.Contains(r.CriticalPaths, p => p.TaskIds.SequenceEqual([a.Id, c.Id]));
        Assert.Contains(r.CriticalPaths, p => p.TaskIds.SequenceEqual([b.Id, d.Id]));
        Assert.Equal(4, r.CriticalTaskCount);
    }

    [Fact]
    public void Standalone_scheduled_tasks_are_outside_the_network_but_set_planned_completion()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var s = Task("Standalone", "2026-10-05", "2026-10-12");
        var r = Run([a, b, s], [Link(a, b)]);

        Assert.False(r.Row(s).InNetwork);
        Assert.False(r.Row(s).IsCritical);
        Assert.Null(r.Row(s).TotalFloat);
        Assert.Equal(D("2026-10-09"), r.Schedule.NetworkCompletion);
        Assert.Equal(D("2026-10-12"), r.Schedule.PlannedCompletion);
        Assert.Contains(s.Id, r.Warning(PlanIssueCodes.NoDependencies)!.TaskIds);
        Assert.Contains(s.Id, r.Warning(PlanIssueCodes.StandaloneAfterNetwork)!.TaskIds);
        Assert.Single(r.CriticalPaths);
    }

    [Fact]
    public void Cancelled_and_undated_tasks_are_left_out()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var cancelled = Task("Cancelled", "2026-10-09", "2026-10-30", TaskItemStatus.Cancelled);
        var undated = Task("Undated", null, null);
        var r = Run([a, b, cancelled, undated], [Link(a, b), Link(b, cancelled), Link(b, undated)]);

        Assert.DoesNotContain(r.Tasks, t => t.TaskId == cancelled.Id || t.TaskId == undated.Id);
        Assert.Equal(D("2026-10-09"), r.Schedule.PlannedCompletion);
        Assert.Contains(undated.Id, r.Warning(PlanIssueCodes.Unscheduled)!.TaskIds);
        Assert.Contains(undated.Id, r.Warning(PlanIssueCodes.LinkNotAnalysed)!.TaskIds);
    }

    [Fact]
    public void Near_critical_uses_the_configured_threshold()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-06", "2026-10-09");
        var x = Task("X", "2026-10-05", "2026-10-07");
        var y = Task("Y", "2026-10-07", "2026-10-08"); // ends Thu: one day of float against Fri
        var links = new[] { Link(a, b), Link(x, y) };

        var near = Run([a, b, x, y], links);
        Assert.True(near.Row(y).IsNearCritical);
        Assert.Equal(1, near.Row(y).TotalFloat);
        Assert.Equal(2, near.NearCriticalTaskCount);

        var strict = Run([a, b, x, y], links, options: new CriticalPathOptions { NearCriticalThresholdWorkingDays = 0 });
        Assert.False(strict.Row(y).IsNearCritical);
    }

    [Fact]
    public void Free_float_is_the_slack_to_the_nearest_successor()
    {
        var a = Task("A", "2026-10-05", "2026-10-06");
        var b = Task("B", "2026-10-08", "2026-10-09");
        var r = Run([a, b], [Link(a, b)]);

        Assert.Equal(2, r.Row(a).FreeFloat);  // Tue 6 -> Thu 8
        Assert.Equal(2, r.Row(a).TotalFloat);
        Assert.Equal(0, r.Row(b).FreeFloat);
    }

    [Fact] // §8.13: another predecessor still controls the successor
    public void Recovery_opportunity_requires_the_successor_to_be_free_to_move()
    {
        var a = Task("A", "2026-10-05", "2026-10-06", estimateMinutes: 60);
        var b = Task("B", "2026-10-05", "2026-10-06");
        var c = Task("C", "2026-10-06", "2026-10-09");
        var held = Run([a, b, c], [Link(a, c), Link(b, c)]);
        var o = Assert.Single(held.Opportunities);
        Assert.True(o.IsCritical);
        Assert.False(o.IsRecoveryOpportunity);
        var successor = Assert.Single(o.Successors);
        Assert.False(successor.CanPropagate);
        Assert.Contains("B", successor.DependencyReadiness);

        var b2 = Task("B", "2026-10-05", "2026-10-05"); // B now finishes a day before C needs it
        var free = Run([a, b2, c], [Link(a, c), Link(b2, c)]);
        var o2 = Assert.Single(free.Opportunities);
        Assert.True(o2.IsRecoveryOpportunity);
        Assert.True(o2.Successors.Single().CanPropagate);
        Assert.Equal("Requires management confirmation.", o2.Successors.Single().ResourceReadiness);
    }

    [Fact]
    public void Early_actual_completion_of_a_critical_predecessor_is_noted()
    {
        var a = Task("A", "2026-10-05", "2026-10-06", TaskItemStatus.Done, completedAt: new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc));
        var b = Task("B", "2026-10-06", "2026-10-09");
        var r = Run([a, b], [Link(a, b)]);

        var note = Assert.Single(r.EarlyCompletions);
        Assert.Equal(b.Id, note.SuccessorId);
        Assert.Equal(D("2026-10-05"), note.CompletedOn);
    }

    [Fact]
    public void Wide_window_and_parent_window_warnings()
    {
        var parent = Task("Parent", "2026-10-06", "2026-10-09", estimateMinutes: 60);
        var child = Task("Child", "2026-10-05", "2026-10-09", parentId: parent.Id);
        var wide = Task("Wide", "2026-10-05", "2026-10-16", estimateMinutes: 120); // 10 working days for 2h
        var r = Run([parent, child, wide], [Link(child, wide)]);

        Assert.Contains(parent.Id, r.Warning(PlanIssueCodes.ParentWindow)!.TaskIds);
        Assert.Contains(wide.Id, r.Warning(PlanIssueCodes.WideWindow)!.TaskIds);
        Assert.DoesNotContain(parent.Id, r.Warning(PlanIssueCodes.WideWindow)!.TaskIds); // 4 days for 1h is wide but under 5 days
    }

    [Fact]
    public void No_scheduled_task_blocks_the_analysis()
    {
        var r = Run([Task("A", null, null)]);
        Assert.True(r.Blocked);
        Assert.Equal(PlanIssueCodes.NoScheduledTasks, r.Errors.Single().Code);
    }

    [Fact]
    public void Result_is_deterministic()
    {
        var a = Task("A", "2026-10-05", "2026-10-06", estimateMinutes: 60);
        var b = Task("B", "2026-10-06", "2026-10-09");
        var links = new[] { Link(a, b) };
        var first = Run([a, b], links, target: "2026-10-30", buffer: 5);
        var second = Run([a, b], links, target: "2026-10-30", buffer: 5);

        Assert.Equal(first.InputFingerprint, second.InputFingerprint);
        Assert.Equal(first.Tasks, second.Tasks);
        Assert.Equal(first.Schedule.BufferStatus, second.Schedule.BufferStatus);
    }
}

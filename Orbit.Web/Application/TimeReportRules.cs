using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the time reports (§12): turns the aggregates <c>ReportingService</c> reads in SQL into report rows.
/// <list type="bullet">
/// <item>Estimates are compared over tasks that have one and aren't Cancelled, against all time logged on them to date.</item>
/// <item>Every task stands alone: subtask time isn't rolled into the parent (§6.10).</item>
/// <item>A task several people worked on shows under each of them, but totals count it once.</item>
/// </list>
/// </summary>
public static class TimeReportRules
{
    /// <summary>Estimated against actual over <paramref name="tasks"/>; each task should appear once.</summary>
    public static EstimateComparison Compare(IEnumerable<TaskTimeFacts> tasks)
    {
        var estimated = tasks.Where(t => t.EstimateMinutes is not null && t.Status != TaskItemStatus.Cancelled).ToList();
        return new EstimateComparison(
            estimated.Count,
            estimated.Sum(t => t.EstimateMinutes!.Value),
            estimated.Sum(t => t.TotalMinutes),
            estimated.Count(t => t.TotalMinutes > t.EstimateMinutes!.Value));
    }

    /// <summary>
    /// Time by person: one row per person with time in the range, ordered by time logged (most first) then name;
    /// each row's tasks ordered by that person's time on them.
    /// </summary>
    public static TimeByPersonReport TimeByPerson(
        IEnumerable<LoggedTime> logged, IReadOnlyDictionary<Guid, TaskTimeFacts> tasks, Func<Guid?, string> name)
    {
        // Merge duplicates defensively (the SQL groups by user and task already) and drop tasks we know nothing about.
        var entries = logged.Where(l => tasks.ContainsKey(l.TaskId))
            .GroupBy(l => (l.UserId, l.TaskId))
            .Select(g => new LoggedTime(g.Key.UserId, g.Key.TaskId, g.Sum(l => l.Minutes)))
            .ToList();

        var rows = entries.GroupBy(l => l.UserId)
            .Select(g =>
            {
                var facts = g.Select(l => tasks[l.TaskId]).ToList();
                var lines = g.Select(l =>
                    {
                        var t = tasks[l.TaskId];
                        return new PersonTaskTimeRow(t.Id, t.Number, t.Title, t.Status, t.EstimateMinutes, l.Minutes, t.TotalMinutes);
                    })
                    .OrderByDescending(r => r.LoggedMinutes).ThenBy(r => r.Number, StringComparer.Ordinal)
                    .ToList();
                return new PersonTimeRow(g.Key, name(g.Key), g.Sum(l => l.Minutes), facts.Count,
                    facts.Count(t => t.EstimateMinutes is null), Compare(facts), lines);
            })
            .OrderByDescending(r => r.LoggedMinutes).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var distinct = entries.Select(l => l.TaskId).Distinct().Select(id => tasks[id]).ToList();
        return new TimeByPersonReport(rows, entries.Sum(l => l.Minutes), distinct.Count,
            distinct.Count(t => t.EstimateMinutes is null), Compare(distinct));
    }

    /// <summary>
    /// Estimate accuracy: completed tasks grouped by assignee (no assignee = "Unassigned"), ordered by count then
    /// name; each row's tasks with the largest overrun first and unestimated ones last.
    /// </summary>
    public static EstimateAccuracyReport EstimateAccuracy(IEnumerable<TaskTimeFacts> doneTasks, Func<Guid?, string> name)
    {
        var tasks = doneTasks.DistinctBy(t => t.Id).ToList();
        var rows = tasks.GroupBy(t => t.AssigneeId)
            .Select(g => new AssigneeAccuracyRow(g.Key, name(g.Key), g.Count(), Compare(g),
                g.Select(t => new TaskEstimateRow(t.Id, t.Number, t.Title, t.EstimateMinutes, t.TotalMinutes))
                    .OrderBy(r => r.VarianceMinutes is null)
                    .ThenByDescending(r => r.VarianceMinutes)
                    .ThenBy(r => r.Number, StringComparer.Ordinal)
                    .ToList()))
            .OrderByDescending(r => r.DoneCount).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new EstimateAccuracyReport(rows, tasks.Count, Compare(tasks));
    }
}

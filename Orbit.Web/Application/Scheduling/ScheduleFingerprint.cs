using System.Security.Cryptography;
using System.Text;
using Orbit.Data.Entities;

namespace Orbit.Application.Scheduling;

/// <summary>
/// A hash of exactly the inputs that drive a critical path analysis (spec §6.17): the dated, non-cancelled tasks and
/// their dates, the links between them, the target date and required buffer, the working week, and the calendar
/// exceptions near the project's dates. A stored analysis whose fingerprint no longer matches is out of date.
/// Estimates, statuses other than Cancelled, comments and time entries are deliberately not part of it.
/// </summary>
public static class ScheduleFingerprint
{
    /// <summary>Calendar exceptions this many days either side of the project's dates count; further away they can't affect it.</summary>
    private const int CalendarPaddingDays = 60;

    public static string Compute(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency> links, DateOnly? targetDate, int? requiredBufferWorkingDays, WorkDayCalendar calendar)
    {
        var dated = tasks
            .Where(t => t.Status != TaskItemStatus.Cancelled && (t.StartDate is not null || t.DueDate is not null))
            .OrderBy(t => t.Id)
            .ToList();
        var ids = dated.Select(t => t.Id).ToHashSet();
        var sb = new StringBuilder();
        foreach (var t in dated)
            sb.Append("T|").Append(t.Id).Append('|').Append(t.StartDate?.DayNumber).Append('|').Append(t.DueDate?.DayNumber).Append('\n');
        foreach (var l in links.Where(l => ids.Contains(l.PredecessorTaskId) && ids.Contains(l.SuccessorTaskId)).OrderBy(l => l.Id))
            sb.Append("L|").Append(l.Id).Append('|').Append(l.PredecessorTaskId).Append('|').Append(l.SuccessorTaskId)
                .Append('|').Append(l.Type).Append('|').Append(l.LagDays).Append('\n');
        sb.Append("P|").Append(targetDate?.DayNumber).Append('|').Append(requiredBufferWorkingDays).Append('\n');
        sb.Append("W|").Append(string.Join(',', calendar.WorkingWeek.Select(d => (int)d))).Append('\n');

        var dates = dated.SelectMany(t => new[] { t.StartDate, t.DueDate }).Where(d => d is not null).Select(d => d!.Value).ToList();
        if (targetDate is DateOnly target) dates.Add(target);
        if (dates.Count > 0)
        {
            var from = dates.Min().AddDays(-CalendarPaddingDays);
            var to = dates.Max().AddDays(CalendarPaddingDays);
            foreach (var x in calendar.ExceptionsBetween(from, to))
                sb.Append("X|").Append(x.Date.DayNumber).Append('|').Append(x.IsWorking ? 1 : 0).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

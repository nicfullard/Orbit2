using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data.Entities;

namespace Orbit.Tests.Scheduling;

/// <summary>
/// Builders for engine tests. Dates are in October 2026: Mon 5, Tue 6, Wed 7, Thu 8, Fri 9, (Sat 10, Sun 11), Mon 12 ...
/// Fri 30, Sat 31. Link day rule (spec §6.15): an FS successor may start on its predecessor's due day.
/// </summary>
internal static class Fixture
{
    public static readonly Guid ProjectId = Guid.NewGuid();
    public static readonly DateOnly Today = D("2026-10-01");

    public static DateOnly D(string iso) => DateOnly.Parse(iso);

    public static TaskItem Task(string title, string? start, string? due, TaskItemStatus status = TaskItemStatus.Todo, int? estimateMinutes = null, Guid? parentId = null, DateTime? completedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ProjectId,
        Title = title,
        Status = status,
        StartDate = start is null ? null : D(start),
        DueDate = due is null ? null : D(due),
        EstimateMinutes = estimateMinutes,
        ParentTaskId = parentId,
        CompletedAt = completedAt
    };

    public static TaskDependency Link(TaskItem predecessor, TaskItem successor, DependencyType type = DependencyType.FinishToStart, int lag = 0) => new()
    {
        Id = Guid.NewGuid(),
        PredecessorTaskId = predecessor.Id,
        Predecessor = predecessor,
        SuccessorTaskId = successor.Id,
        Successor = successor,
        Type = type,
        LagDays = lag
    };

    public static CriticalPathResult Run(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency>? links = null, string? target = null, int? buffer = null,
        WorkDayCalendar? calendar = null, CriticalPathOptions? options = null) =>
        CriticalPathEngine.Analyse(new CriticalPathInput(
            ProjectId, target is null ? null : D(target), buffer, tasks.ToList(), (links ?? []).ToList(),
            calendar ?? WorkDayCalendar.Default, Today, options ?? new CriticalPathOptions()));

    public static TaskAnalysis Row(this CriticalPathResult r, TaskItem t) => r.Tasks.Single(x => x.TaskId == t.Id);

    public static PlanIssue? Warning(this CriticalPathResult r, string code) => r.Warnings.FirstOrDefault(w => w.Code == code);
}

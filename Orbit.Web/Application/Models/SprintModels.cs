using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class SprintInput
{
    public string Name { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}

public sealed record NameCount(string Name, int Count);

public sealed record SprintListItem(Sprint Sprint, int TotalTasks, int OpenTasks, IReadOnlyList<NameCount> ByDepartment)
{
    public int DoneTasks => TotalTasks - OpenTasks;
}

public sealed record SprintBoard(Sprint Sprint, IReadOnlyList<SprintBoardColumn> Columns)
{
    public int Total => Columns.Sum(c => c.Tasks.Count);
}

public sealed record SprintBoardColumn(string Title, TaskItemStatus Status, IReadOnlyList<TaskItem> Tasks);

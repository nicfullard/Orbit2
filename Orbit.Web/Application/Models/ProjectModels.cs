using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class ProjectFilter
{
    public Guid? DepartmentId { get; set; }
    public ProjectStatus? Status { get; set; }
    public bool IncludeArchived { get; set; }
    public string? Search { get; set; }
}

public sealed record ProjectListItem(Project Project, int TotalTasks, int OpenTasks, int DoneTasks)
{
    public int PercentDone => TotalTasks == 0 ? 0 : (int)Math.Round(DoneTasks * 100.0 / TotalTasks);
}

public sealed class ProjectInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    /// <summary>Defaults to the caller.</summary>
    public Guid? OwnerId { get; set; }
    public DateOnly? TargetDate { get; set; }
}

/// <summary>Task counts for one department represented on a project (§6.2.1 cross-department project tasks).</summary>
public sealed record DepartmentTaskCount(
    Guid DepartmentId,
    string Name,
    int Todo,
    int InProgress,
    int Blocked,
    int Done,
    int Cancelled,
    int Overdue)
{
    public int Total => Todo + InProgress + Blocked + Done + Cancelled;
    public int Open => Todo + InProgress + Blocked;
    public int PercentDone => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);
}

public sealed record ProjectStatusSummary(
    Guid Id,
    string Name,
    ProjectStatus Status,
    string DepartmentName,
    Guid DepartmentId,
    string OwnerName,
    DateOnly? TargetDate,
    int Total,
    int Todo,
    int InProgress,
    int Blocked,
    int Done,
    int Cancelled,
    int Overdue,
    int TotalMinutesLogged,
    /// <summary>Sum of the tasks' estimates (§6.10), cancelled tasks excluded.</summary>
    int TotalMinutesEstimated,
    IReadOnlyList<DepartmentTaskCount> ByDepartment)
{
    public int Open => Todo + InProgress + Blocked;
    public int PercentDone => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);
    /// <summary>True when tasks from a department other than the project's own are filed under it.</summary>
    public bool IsCrossDepartment => ByDepartment.Any(d => d.DepartmentId != DepartmentId);
}

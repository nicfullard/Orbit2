using System.ComponentModel.DataAnnotations;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public sealed class TaskForm
{
    [Required, StringLength(300)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Display(Name = "Project")]
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Blank = default (the project's department, or the caller's own for a standalone task). A System Admin
    /// may pick a department other than the project's to file a cross-department project task (§6.2.1).
    /// </summary>
    [Display(Name = "Department")]
    public Guid? DepartmentId { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public TaskType Type { get; set; } = TaskType.Task;

    /// <summary>Estimated effort in minutes (§6.10); blank or 0 = no estimate.</summary>
    [Display(Name = "Estimate (minutes)"), Range(0, 525600)]
    public int? EstimateMinutes { get; set; }

    [Display(Name = "Assignee")]
    public Guid? AssigneeId { get; set; }

    [Display(Name = "Start date"), DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "Due date"), DataType(DataType.Date)]
    public DateOnly? DueDate { get; set; }

    /// <summary>Makes this a subtask (§6.15): a task on the same project, or a standalone task in the same department.</summary>
    [Display(Name = "Parent task")]
    public Guid? ParentTaskId { get; set; }

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    [Display(Name = "Sprint")]
    public Guid? SprintId { get; set; }

    /// <summary>The asset this task is about (§6.19); blank = none. Chosen with the asset picker.</summary>
    [Display(Name = "Asset")]
    public Guid? AssetId { get; set; }

    public TaskInput ToInput(bool includeStatus) => new()
    {
        Title = Title,
        Description = Description,
        ProjectId = ProjectId,
        DepartmentId = DepartmentId,
        Priority = Priority,
        Type = Type,
        EstimateMinutes = EstimateMinutes,
        AssigneeId = AssigneeId,
        StartDate = StartDate,
        DueDate = DueDate,
        ParentTaskId = ParentTaskId,
        Status = includeStatus ? Status : null,
        SprintId = SprintId,
        AssetId = AssetId
    };

    public static TaskForm From(TaskItem t) => new()
    {
        Title = t.Title,
        Description = t.Description,
        ProjectId = t.ProjectId,
        DepartmentId = t.DepartmentId,
        Priority = t.Priority,
        Type = t.Type,
        EstimateMinutes = t.EstimateMinutes,
        AssigneeId = t.AssigneeId,
        StartDate = t.StartDate,
        DueDate = t.DueDate,
        ParentTaskId = t.ParentTaskId,
        Status = t.Status,
        SprintId = t.SprintId,
        AssetId = t.AssetId
    };
}

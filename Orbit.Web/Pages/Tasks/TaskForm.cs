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

    public TaskInput ToInput(bool includeStatus) => new()
    {
        Title = Title,
        Description = Description,
        ProjectId = ProjectId,
        DepartmentId = DepartmentId,
        Priority = Priority,
        Type = Type,
        AssigneeId = AssigneeId,
        StartDate = StartDate,
        DueDate = DueDate,
        ParentTaskId = ParentTaskId,
        Status = includeStatus ? Status : null,
        SprintId = SprintId
    };

    public static TaskForm From(TaskItem t) => new()
    {
        Title = t.Title,
        Description = t.Description,
        ProjectId = t.ProjectId,
        DepartmentId = t.DepartmentId,
        Priority = t.Priority,
        Type = t.Type,
        AssigneeId = t.AssigneeId,
        StartDate = t.StartDate,
        DueDate = t.DueDate,
        ParentTaskId = t.ParentTaskId,
        Status = t.Status,
        SprintId = t.SprintId
    };
}

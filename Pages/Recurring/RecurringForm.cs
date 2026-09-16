using System.ComponentModel.DataAnnotations;
using Orbit.Application.Models;
using Orbit.Data.Entities;
using Orbit.Pages.Tasks;

namespace Orbit.Pages.Recurring;

public sealed class RecurringForm
{
    [Required, StringLength(300)] public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>Blank = default (the project's department, or the caller's own). System Admins may choose another (§6.2.1).</summary>
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public Guid? AssigneeId { get; set; }
    [Required, StringLength(500)] public string RecurrenceRule { get; set; } = "FREQ=WEEKLY;BYDAY=MO";
    [DataType(DataType.Date)] public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [Range(0, 365)] public int LeadTimeDays { get; set; } = 1;

    public RecurringInput ToInput() => new()
    {
        Title = Title, Description = Description, ProjectId = ProjectId,
        DepartmentId = DepartmentId,
        Priority = Priority, AssigneeId = AssigneeId, RecurrenceRule = RecurrenceRule,
        StartDate = StartDate, LeadTimeDays = LeadTimeDays
    };

    public TaskForm AsTaskForm() => new() { ProjectId = ProjectId, DepartmentId = DepartmentId, AssigneeId = AssigneeId, Priority = Priority };

    public static RecurringForm From(RecurringTaskDefinition d) => new()
    {
        Title = d.Title, Description = d.Description, ProjectId = d.ProjectId, DepartmentId = d.DepartmentId,
        Priority = d.Priority, AssigneeId = d.AssigneeId, RecurrenceRule = d.RecurrenceRule,
        StartDate = d.StartDate, LeadTimeDays = d.LeadTimeDays
    };
}

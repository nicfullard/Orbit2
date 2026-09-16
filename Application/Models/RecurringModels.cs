using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class RecurringInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public Guid? AssigneeId { get; set; }
    public string RecurrenceRule { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public int LeadTimeDays { get; set; }
}

public sealed class RecurringFilter
{
    public Guid? ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool IncludeInactive { get; set; } = true;
}

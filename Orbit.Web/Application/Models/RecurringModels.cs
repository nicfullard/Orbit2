using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class RecurringInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    /// <summary>The complete set of assignees each generated task gets (§6.2.3), under a task's rule. Null on update keeps them.</summary>
    public IReadOnlyList<Guid>? AssigneeIds { get; set; }
    /// <summary>The asset each generated task is about (§6.19); the same rule as a task's asset. Null = none.</summary>
    public Guid? AssetId { get; set; }
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

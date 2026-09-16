using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>Model for the shared task table partial.</summary>
public sealed class TaskRowsVm
{
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
    public required Actor Actor { get; init; }
    public required string ReturnUrl { get; init; }
    public bool ShowProject { get; init; } = true;
    public bool ShowDepartment { get; init; } = true;
    public bool ShowSprint { get; init; }
    public bool ShowAssignee { get; init; } = true;
    /// <summary>When set, each plannable row gets a checkbox bound (via the HTML form attribute) to the form with this id.</summary>
    public string? SelectionFormId { get; init; }
    public string? EmptyMessage { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}

using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>Model for the shared task table partial.</summary>
public sealed class TaskRowsVm
{
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
    public required Actor Actor { get; init; }
    public required string ReturnUrl { get; init; }
    /// <summary>
    /// When set, rows the actor may edit get inline assignee and due-date controls (like the inline status control).
    /// Candidates are filtered per row to the task's department plus System Admins. Null = read-only cells.
    /// </summary>
    public IReadOnlyList<UserSummary>? QuickEditAssignees { get; init; }
    /// <summary>
    /// Inline due-date control on rows the actor may edit, without the assignee control (My Tasks, where every row is
    /// the viewer's own). Implied when <see cref="QuickEditAssignees"/> is set.
    /// </summary>
    public bool QuickEditDueDate { get; init; }
    /// <summary>Show the "Today" day-plan checkbox column (§6.12) on open rows the actor may plan.</summary>
    public bool ShowPlanToday { get; init; }
    public bool ShowProject { get; init; } = true;
    public bool ShowDepartment { get; init; } = true;
    public bool ShowSprint { get; init; }
    public bool ShowAssignee { get; init; } = true;
    /// <summary>Show each task's estimated effort (§6.10) as an "Est." column - the Today page's load view (§6.12).</summary>
    public bool ShowEstimate { get; init; }
    /// <summary>When set, each plannable row gets a checkbox bound (via the HTML form attribute) to the form with this id.</summary>
    public string? SelectionFormId { get; init; }
    /// <summary>Tasks whose next move is gated by a dependency (§6.15), with the reason for the Gated badge. Null = badge not computed.</summary>
    public IReadOnlyDictionary<Guid, WaitingSummary>? Waiting { get; init; }
    /// <summary>How many times each task has been carried over on the day plan (§6.12), for the "Carried over" badge. Null = badge not computed.</summary>
    public IReadOnlyDictionary<Guid, int>? CarryOvers { get; init; }
    public string? EmptyMessage { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>
/// Model for the dashboard's My waiting approvals card (spec §6.9): the request approvals asked of the viewer, oldest first, and
/// who the viewer is, so a request they logged themselves reads "logged by you".
/// </summary>
public sealed record WaitingApprovalsVm(IReadOnlyList<RequestActionItem> Items, Guid? MeId)
{
    /// <summary>How many rows the card lists before it links to the Requests page for the rest.</summary>
    public const int Shown = 8;
}

using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed record DepartmentSprintProgress(string Department, int Total, int Done);

public sealed record DepartmentLoad(Guid DepartmentId, string Name, int OpenTasks, int ActiveProjects, int Users);

public sealed class DashboardModel
{
    public required Actor Actor { get; init; }
    public required DateOnly Today { get; init; }
    public string? DepartmentName { get; init; }

    /// <summary>"My tasks" for Members; department/company-wide for admins.</summary>
    public required string ScopeLabel { get; init; }
    public int TodoCount { get; init; }
    public int InProgressCount { get; init; }
    public int BlockedCount { get; init; }
    public int OverdueCount { get; init; }
    /// <summary>Today's day plan (§6.12) within the same role scope, counting closed tasks too so "done today" shows.</summary>
    public int PlannedTodayCount { get; init; }
    public int PlannedTodayDone { get; init; }
    public IReadOnlyList<TaskItem> OpenTasks { get; init; } = [];

    /// <summary>The caller's own open tasks (shown separately for admins whose main widget is scope-wide).</summary>
    public IReadOnlyList<TaskItem> MyOpenTasks { get; init; } = [];

    /// <summary>Open, unassigned tasks in a Member's department - work they may take (§6.5). Empty for admins, whose main widget already covers the department.</summary>
    public IReadOnlyList<TaskItem> UpForGrabs { get; init; } = [];

    public Sprint? ActiveSprint { get; init; }
    public IReadOnlyList<TaskItem> SprintTasks { get; init; } = [];
    public int SprintTotal { get; init; }
    public int SprintDone { get; init; }
    public IReadOnlyList<DepartmentSprintProgress> SprintByDepartment { get; init; } = [];

    public IReadOnlyList<ProjectListItem> Projects { get; init; } = [];
    public IReadOnlyList<TaskItem> DueSoon { get; init; } = [];

    public IReadOnlyList<NameCount> OpenByAssignee { get; init; } = [];
    public IReadOnlyList<AuditLog> Activity { get; init; } = [];
    public IReadOnlyList<DepartmentLoad> ByDepartment { get; init; } = [];
}

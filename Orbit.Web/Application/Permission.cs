namespace Orbit.Application;

/// <summary>
/// The permission keys (spec §6.5). Each is a string stored as-is on a role's grants, so adding one needs no
/// migration; the catalogue (<see cref="PermissionCatalog"/>) says what each one gates and which scopes it allows.
/// </summary>
public static class Permission
{
    public const string TasksView = "tasks.view";
    public const string TasksCreate = "tasks.create";
    public const string TasksEdit = "tasks.edit";
    public const string TasksTake = "tasks.take";
    public const string TasksPlan = "tasks.plan";
    public const string ProjectsView = "projects.view";
    public const string ProjectsCreate = "projects.create";
    public const string ProjectsEdit = "projects.edit";
    public const string TimeLog = "time.log";
    public const string SprintsManage = "sprints.manage";
    public const string ReportsView = "reports.view";
    public const string AuditView = "audit.view";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string ApiKeysManage = "api_keys.manage";
    public const string DepartmentsManage = "departments.manage";
    public const string CalendarManage = "calendar.manage";
    public const string DirectoryManage = "directory.manage";
    public const string AgentsManage = "agents.manage";
}

/// <summary>
/// How far a grant reaches (spec §6.5). A grant at a scope covers the lower ones: <see cref="Own"/> is the actor's own
/// objects (tasks assigned to or created by them, projects they own, their own time entries), <see cref="Department"/>
/// everything in their own department, <see cref="All"/> every department. Stored as its name.
/// </summary>
public enum PermissionScope
{
    None = 0,
    Own = 1,
    Department = 2,
    All = 3
}

public static class PermissionScopeExtensions
{
    public static string Label(this PermissionScope scope) => scope switch
    {
        PermissionScope.Own => "Own",
        PermissionScope.Department => "Department",
        PermissionScope.All => "All departments",
        _ => "None"
    };
}

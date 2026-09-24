namespace Orbit.Application;

/// <summary>One permission as the role editor and the tests see it (spec §6.5).</summary>
/// <param name="Key">The stored key, e.g. <c>tasks.edit</c>.</param>
/// <param name="AllowedScopes">The scopes a role may grant it at; org-level permissions allow only <see cref="PermissionScope.All"/>.</param>
/// <param name="SystemAdministratorOnly">Reserved to the built-in role: never grantable on a custom role.</param>
public sealed record PermissionDefinition(
    string Key,
    string Label,
    string Description,
    string Group,
    IReadOnlyList<PermissionScope> AllowedScopes,
    bool SystemAdministratorOnly = false)
{
    /// <summary>True for a permission that only makes sense company-wide; the role editor renders it as a checkbox.</summary>
    public bool AllOnly => AllowedScopes.Count == 1 && AllowedScopes[0] == PermissionScope.All;

    public bool Allows(PermissionScope scope) => AllowedScopes.Contains(scope);
}

/// <summary>
/// The fixed, code-defined catalogue of permissions (spec §6.5). Roles are data (a named set of grants, each at a scope);
/// what a grant means is defined here and in <see cref="AccessPolicy"/>. The built-in System Administrator role holds
/// every entry at <see cref="PermissionScope.All"/>, including ones added by later releases.
/// </summary>
public static class PermissionCatalog
{
    public const string TasksGroup = "Tasks";
    public const string ProjectsGroup = "Projects";
    public const string TimeGroup = "Time tracking";
    public const string PlanningGroup = "Planning";
    public const string InsightGroup = "Reports and audit";
    public const string AdministrationGroup = "Administration";

    private static readonly PermissionScope[] OwnDeptAll = [PermissionScope.Own, PermissionScope.Department, PermissionScope.All];
    private static readonly PermissionScope[] DeptAll = [PermissionScope.Department, PermissionScope.All];
    private static readonly PermissionScope[] AllOnlyScopes = [PermissionScope.All];

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permission.TasksView, "View tasks",
            "See tasks and recurring task definitions, their comments, attachments and activity. Its scope also sets the dashboard tier: Own = personal, Department = department, All = company-wide.",
            TasksGroup, OwnDeptAll),
        new(Permission.TasksCreate, "Create tasks",
            "Create tasks and recurring task definitions. At All departments it also allows filing a task for another department under a project (a cross-department project task) and choosing the department on the form.",
            TasksGroup, DeptAll),
        new(Permission.TasksEdit, "Edit tasks",
            "Edit any field of a task, including every status change (closing and reopening too), its parent, its dependencies and its assignee; recurring definitions likewise. Own = tasks assigned to you or created by you.",
            TasksGroup, OwnDeptAll),
        new(Permission.TasksTake, "Take unassigned tasks",
            "Assign an open, unassigned task to yourself without edit rights on it.",
            TasksGroup, DeptAll),
        new(Permission.TasksPlan, "Plan tasks",
            "Move tasks between the backlog and a sprint, and put them on the day plan (the Today tick).",
            PlanningGroup, OwnDeptAll),
        new(Permission.ProjectsView, "View projects",
            "See projects, their files and their critical path results. Own = projects you own. Department also shows, read-only, other departments' projects that have tasks in yours.",
            ProjectsGroup, OwnDeptAll),
        new(Permission.ProjectsCreate, "Create projects",
            "Create projects in the department, or anywhere at All departments.",
            ProjectsGroup, DeptAll),
        new(Permission.ProjectsEdit, "Edit projects",
            "Edit and archive projects and run their critical path analysis. Own = projects you own. Moving a project to another department needs All departments.",
            ProjectsGroup, OwnDeptAll),
        new(Permission.TimeLog, "Log time",
            "Log, edit and delete time. Own = your own entries on tasks assigned to you; Department = for anyone in the department.",
            TimeGroup, OwnDeptAll),
        new(Permission.SprintsManage, "Manage sprints",
            "Create, edit, start and complete sprints. Sprints are company-wide, so this is all or nothing.",
            PlanningGroup, AllOnlyScopes),
        new(Permission.ReportsView, "View reports",
            "The Reports pages and their PDF exports. At Department the department filter is fixed to your own department.",
            InsightGroup, DeptAll),
        new(Permission.AuditView, "View the activity log",
            "Admin > Activity Log, and the list_activity tool. At Department only your department's entries.",
            InsightGroup, DeptAll),
        new(Permission.UsersManage, "Manage users",
            "Create users, change their role, department and sign-in method, deactivate, unlock and reset passwords.",
            AdministrationGroup, AllOnlyScopes, SystemAdministratorOnly: true),
        new(Permission.RolesManage, "Manage roles",
            "Admin > Roles: create, edit and delete roles and their permissions.",
            AdministrationGroup, AllOnlyScopes, SystemAdministratorOnly: true),
        new(Permission.ApiKeysManage, "Manage API keys",
            "Issue and revoke the API keys Claude connects with.",
            AdministrationGroup, AllOnlyScopes, SystemAdministratorOnly: true),
        new(Permission.DepartmentsManage, "Manage departments",
            "Create, edit and archive departments, and see the department overview pages.",
            AdministrationGroup, AllOnlyScopes),
        new(Permission.CalendarManage, "Manage the working calendar",
            "The organisation's working week and public holidays, used by the critical path analysis.",
            AdministrationGroup, AllOnlyScopes),
        new(Permission.DirectoryManage, "Manage directory sign-in",
            "Admin > Directory: the LDAP / Active Directory settings.",
            AdministrationGroup, AllOnlyScopes),
        new(Permission.AgentsManage, "Manage Orbit Agents",
            "Register, revoke and delete the on-premises Orbit Agents.",
            AdministrationGroup, AllOnlyScopes)
    ];

    public static readonly IReadOnlyDictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>Every permission at All: what the built-in role holds, and what <see cref="Actor.System"/> runs with.</summary>
    public static readonly IReadOnlyDictionary<string, PermissionScope> AllAtScopeAll =
        All.ToDictionary(p => p.Key, _ => PermissionScope.All, StringComparer.Ordinal);

    /// <summary>The permissions that put the Admin menu on the navbar; each item is then shown on its own permission.</summary>
    public static readonly IReadOnlyList<string> AdminPermissions =
    [
        Permission.UsersManage, Permission.RolesManage, Permission.DepartmentsManage, Permission.ApiKeysManage,
        Permission.DirectoryManage, Permission.AgentsManage, Permission.CalendarManage, Permission.AuditView
    ];

    public static PermissionDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The catalogue in display order, grouped for the role editor.</summary>
    public static IEnumerable<IGrouping<string, PermissionDefinition>> Groups => All.GroupBy(p => p.Group);
}

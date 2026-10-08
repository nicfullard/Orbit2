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
    public const string AssetsGroup = "Assets";
    public const string RequestsGroup = "Requests";
    public const string AdministrationGroup = "Administration";

    private static readonly PermissionScope[] OwnDeptAll = [PermissionScope.Own, PermissionScope.Department, PermissionScope.All];
    private static readonly PermissionScope[] DeptAll = [PermissionScope.Department, PermissionScope.All];
    private static readonly PermissionScope[] AllOnlyScopes = [PermissionScope.All];
    private static readonly PermissionScope[] OwnDept = [PermissionScope.Own, PermissionScope.Department];

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permission.TasksView, "View tasks",
            "See tasks and recurring task definitions, their comments, attachments and activity. Its scope also sets the dashboard tier: Own = personal, Department = department, All = company-wide.",
            TasksGroup, OwnDeptAll),
        new(Permission.TasksCreate, "Create tasks",
            "Create tasks and recurring task definitions. At All departments it also allows filing a task for another department under a project (a cross-department project task) and choosing the department on the form.",
            TasksGroup, DeptAll),
        new(Permission.TasksCreateFor, "Create tasks for others",
            "Name the requestee - the person a task is for - when creating or editing a task, on the task form or over MCP. The requestee sees, edits and plans the task as their own and is emailed. Own = only yourself, so the field isn't offered; Department = anyone in your department; All = anyone. Separate from Log requests, whose own scope decides whom a request flow may be for.",
            TasksGroup, OwnDeptAll),
        new(Permission.TasksEdit, "Edit tasks",
            "Edit any field of a task, including every status change (closing and reopening too), its parent, its dependencies and its assignee; recurring definitions likewise. Own = tasks assigned to you, created by you or created for you (you are the requestee).",
            TasksGroup, OwnDeptAll),
        new(Permission.TasksTake, "Take unassigned tasks",
            "Assign an open, unassigned task to yourself without edit rights on it.",
            TasksGroup, DeptAll),
        new(Permission.TasksPlan, "Plan tasks",
            "Move tasks between the backlog and a sprint, and put them on the day plan (the Today tick).",
            PlanningGroup, OwnDeptAll),
        new(Permission.ProjectsView, "View projects",
            "See projects, their comments, files and critical path results, and comment on them. Own = projects you own. Department also shows other departments' projects that have tasks in yours - read-only, apart from commenting.",
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
        new(Permission.AssetsView, "View assets",
            "See assets and their checks, comments, files, activity and task history, comment on them, attach files to them and choose them as the asset a task is about. Own = assets assigned to you; Department = the assets your department manages, plus the ones you hold.",
            AssetsGroup, OwnDeptAll),
        new(Permission.AssetsCreate, "Register assets",
            "Register new assets in the department, or in any department at All departments.",
            AssetsGroup, DeptAll),
        new(Permission.AssetsEdit, "Edit assets",
            "Edit any field of an asset - status and disposal, type and properties, location, purchase and warranty details, who holds it - remove anyone's checks and files, and delete an asset registered in error. Moving an asset to another department needs All departments.",
            AssetsGroup, DeptAll),
        new(Permission.AssetsCheck, "Record asset checks",
            "Record a check on an asset, and remove checks you recorded. Own = the assets assigned to you (confirming you still have them).",
            AssetsGroup, OwnDeptAll),
        new(Permission.AssetsConfigure, "Configure assets",
            "Manage the department's asset types (with their properties and check intervals) and asset locations; every department's at All departments.",
            AssetsGroup, DeptAll),
        new(Permission.RequestsSubmit, "Log requests",
            "Log requests through any department's request flows (the Requests page), whatever your Create tasks reach; follow the ones you logged or were addressed in; and act on the steps addressed to you - a form to fill in, a web page to open, an approval to give. Own = your own requests. Department = also the ones anyone in your department logged, under Your requests.",
            RequestsGroup, OwnDept),
        new(Permission.RequestsConfigure, "Configure request flows",
            "Manage the department's request categories and flows: each flow's steps (forms, approvals, tasks, actions, web pages), their fields, approvers and dependencies; every department's at All departments.",
            RequestsGroup, DeptAll),
        new(Permission.RequestsManage, "Manage requests",
            "See every request filed with the department, act on any of its steps, cancel it, and retry or skip a step that failed; every department's at All departments.",
            RequestsGroup, DeptAll),
        new(Permission.ActionsCreate, "Create actions",
            "Admin > Actions: write, change and delete the scripts in the action library that request flows run, and choose where each runs. Scripts are C# that runs as the Orbit server or an Orbit Agent, so this is granted sparingly.",
            RequestsGroup, AllOnlyScopes),
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
            AdministrationGroup, AllOnlyScopes),
        new(Permission.NagiosManage, "Manage Nagios monitoring",
            "Admin > Nagios: the Nagios Core instances Orbit watches through an Orbit Agent - their address and sign-in, how long a host or service may be down before a task is raised, and where those tasks go - and the problems seen on each.",
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
        Permission.DirectoryManage, Permission.AgentsManage, Permission.NagiosManage, Permission.CalendarManage, Permission.RequestsConfigure,
        Permission.ActionsCreate, Permission.AuditView
    ];

    public static PermissionDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The catalogue in display order, grouped for the role editor.</summary>
    public static IEnumerable<IGrouping<string, PermissionDefinition>> Groups => All.GroupBy(p => p.Group);
}

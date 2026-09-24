namespace Orbit.Application;

/// <summary>
/// The roles Orbit ships with (spec §6.5). The built-in System Administrator role holds every permission at All and is
/// not stored as grants; Member and Department Admin are ordinary, editable roles whose default grants reproduce the
/// rights those roles had before roles became data. They are created once, when absent, and never re-applied.
/// </summary>
public static class DefaultRoles
{
    public const string SystemAdministrator = "System Administrator";
    public const string Member = "Member";
    public const string DepartmentAdmin = "Department Admin";

    public const string SystemAdministratorDescription = "Every permission, everywhere. Built in; cannot be edited or deleted.";
    public const string MemberDescription = "Sees their department's work; edits, plans and logs time on their own tasks; takes unassigned tasks; sees and confirms the assets they hold.";
    public const string DepartmentAdminDescription = "Manages every task and project in their own department, including colleagues' time, and the department's asset register.";

    public static readonly IReadOnlyDictionary<string, PermissionScope> MemberGrants = new Dictionary<string, PermissionScope>(StringComparer.Ordinal)
    {
        [Permission.TasksView] = PermissionScope.Department,
        [Permission.TasksCreate] = PermissionScope.Department,
        [Permission.TasksEdit] = PermissionScope.Own,
        [Permission.TasksTake] = PermissionScope.Department,
        [Permission.TasksPlan] = PermissionScope.Department,
        [Permission.ProjectsView] = PermissionScope.Department,
        [Permission.ProjectsCreate] = PermissionScope.Department,
        [Permission.ProjectsEdit] = PermissionScope.Own,
        [Permission.TimeLog] = PermissionScope.Own,
        // Assets (§6.19): see and self-certify the assets they hold.
        [Permission.AssetsView] = PermissionScope.Own,
        [Permission.AssetsCheck] = PermissionScope.Own
    };

    public static readonly IReadOnlyDictionary<string, PermissionScope> DepartmentAdminGrants = new Dictionary<string, PermissionScope>(StringComparer.Ordinal)
    {
        [Permission.TasksView] = PermissionScope.Department,
        [Permission.TasksCreate] = PermissionScope.Department,
        [Permission.TasksEdit] = PermissionScope.Department,
        [Permission.TasksTake] = PermissionScope.Department,
        [Permission.TasksPlan] = PermissionScope.Department,
        [Permission.ProjectsView] = PermissionScope.Department,
        [Permission.ProjectsCreate] = PermissionScope.Department,
        [Permission.ProjectsEdit] = PermissionScope.Department,
        [Permission.TimeLog] = PermissionScope.Department,
        // Assets (§6.19): run the department's register, including its own asset types and locations.
        [Permission.AssetsView] = PermissionScope.Department,
        [Permission.AssetsCreate] = PermissionScope.Department,
        [Permission.AssetsEdit] = PermissionScope.Department,
        [Permission.AssetsCheck] = PermissionScope.Department,
        [Permission.AssetsConfigure] = PermissionScope.Department
    };
}

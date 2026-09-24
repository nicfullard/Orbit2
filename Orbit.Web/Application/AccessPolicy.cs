using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The §6.5 rules as code: which permission, at which scope, each operation needs. Every service consults these,
/// so the Razor Pages UI, the MCP tools and any future REST surface are bound by exactly the same checks.
/// "Own" means assigned to or created by the actor for tasks and recurring definitions, owned by the actor for projects.
/// </summary>
public static class AccessPolicy
{
    private static bool IsOwn(Actor a, TaskItem t) =>
        a.UserId is Guid me && (t.AssigneeId == me || t.CreatedById == me);

    private static bool IsOwn(Actor a, RecurringTaskDefinition d) =>
        a.UserId is Guid me && (d.AssigneeId == me || d.CreatedById == me);

    private static bool IsOwn(Actor a, Project p) => a.UserId is Guid me && p.OwnerId == me;

    public static bool CanViewTask(Actor a, TaskItem t) => a.Can(Permission.TasksView, t.DepartmentId, IsOwn(a, t));

    /// <summary>Edit any field: tasks.edit at Own (assigned or created by the actor), Department or All.</summary>
    public static bool CanEditTask(Actor a, TaskItem t) => a.Can(Permission.TasksEdit, t.DepartmentId, IsOwn(a, t));

    /// <summary>
    /// Taking an unassigned task: tasks.take in the task's department lets a signed-in user assign an open, unassigned
    /// task to themselves. This is the one assignee change someone may make on a task they can't otherwise edit;
    /// once it is theirs, <see cref="CanEditTask"/> applies like any assigned task.
    /// </summary>
    public static bool CanTakeTask(Actor a, TaskItem t) =>
        a.UserId is not null && t.IsOpen && t.AssigneeId is null && a.CanInDepartment(Permission.TasksTake, t.DepartmentId);

    /// <summary>
    /// Changing a task's status, including closing it (Done/Cancelled) and reopening it, follows edit rights (§13 item 35).
    /// </summary>
    public static bool CanChangeStatus(Actor a, TaskItem t) => CanEditTask(a, t);

    /// <summary>Backlog &lt;-&gt; sprint moves and the day plan: tasks.plan within reach.</summary>
    public static bool CanPlanTask(Actor a, TaskItem t) => a.Can(Permission.TasksPlan, t.DepartmentId, IsOwn(a, t));

    /// <summary>Filing a task (or recurring definition) under a project needs tasks.create in the project's department.</summary>
    public static bool CanAddTaskToProject(Actor a, Project p) => a.CanInDepartment(Permission.TasksCreate, p.DepartmentId);

    /// <summary>
    /// A cross-department project task is one whose department differs from its project's (§6.2.1).
    /// Introducing that pairing needs tasks.create everywhere; the task's own department then works it like any other task of theirs.
    /// </summary>
    public static bool CanFileCrossDepartmentTask(Actor a) => a.CanAnywhere(Permission.TasksCreate);

    /// <summary>Creating a task or recurring definition in a department.</summary>
    public static bool CanCreateTaskIn(Actor a, Guid departmentId) => a.CanInDepartment(Permission.TasksCreate, departmentId);

    /// <summary>Moving a task or recurring definition into another department: an edit that must reach the target department.</summary>
    public static bool CanMoveTaskTo(Actor a, Guid departmentId) => a.CanInDepartment(Permission.TasksEdit, departmentId);

    /// <summary>
    /// projects.view at Own (the actor owns it), Department or All. At Department scope a project from another department
    /// that has tasks filed in the actor's department (§6.2.1) is visible too, read-only; pass <paramref name="hasTasksInActorDepartment"/> for that case.
    /// </summary>
    public static bool CanViewProject(Actor a, Project p, bool hasTasksInActorDepartment = false) =>
        a.Can(Permission.ProjectsView, p.DepartmentId, IsOwn(a, p)) ||
        (hasTasksInActorDepartment && a.ScopeOf(Permission.ProjectsView) >= PermissionScope.Department);

    /// <summary>Edit and archive: projects.edit at Own (the actor owns it), Department or All.</summary>
    public static bool CanEditProject(Actor a, Project p) => a.Can(Permission.ProjectsEdit, p.DepartmentId, IsOwn(a, p));

    public static bool CanCreateProjectIn(Actor a, Guid departmentId) => a.CanInDepartment(Permission.ProjectsCreate, departmentId);

    /// <summary>Moving a project to another department needs projects.edit everywhere.</summary>
    public static bool CanMoveProject(Actor a) => a.CanAnywhere(Permission.ProjectsEdit);

    public static bool CanManageSprints(Actor a) => a.CanAnywhere(Permission.SprintsManage);

    /// <summary>Running a critical path analysis (§6.17) records a result on the project, so it follows edit rights; seeing one follows view rights.</summary>
    public static bool CanRunCriticalPath(Actor a, Project p) => CanEditProject(a, p);

    /// <summary>The working calendar is organisation-wide (§6.17).</summary>
    public static bool CanManageWorkingCalendar(Actor a) => a.CanAnywhere(Permission.CalendarManage);

    /// <summary>Recurring definitions follow the task rules: tasks.view to see, tasks.edit to change.</summary>
    public static bool CanViewRecurring(Actor a, RecurringTaskDefinition d) => a.Can(Permission.TasksView, d.DepartmentId, IsOwn(a, d));

    public static bool CanEditRecurring(Actor a, RecurringTaskDefinition d) => a.Can(Permission.TasksEdit, d.DepartmentId, IsOwn(a, d));

    /// <summary>
    /// time.log: at Own, the actor's own time on tasks assigned to them; at Department, for anyone in the task's department; at All, anyone anywhere.
    /// </summary>
    public static bool CanLogTimeFor(Actor a, TaskItem t, Guid targetUserId) =>
        a.CanInDepartment(Permission.TimeLog, t.DepartmentId) ||
        (targetUserId == a.UserId && t.AssigneeId == a.UserId && a.ScopeOf(Permission.TimeLog) >= PermissionScope.Own);

    public static bool CanEditTimeEntry(Actor a, TimeEntry e, TaskItem task) =>
        a.CanInDepartment(Permission.TimeLog, task.DepartmentId) ||
        (a.UserId is Guid me && e.UserId == me && a.ScopeOf(Permission.TimeLog) >= PermissionScope.Own);

    /// <summary>Logging time for somebody else on a task: time.log in the task's department.</summary>
    public static bool CanLogTimeForOthers(Actor a, TaskItem t) => a.CanInDepartment(Permission.TimeLog, t.DepartmentId);

    /// <summary>Attaching a file to a task (§6.18) follows the commenting rule: anyone who can see the task.</summary>
    public static bool CanAttachToTask(Actor a, TaskItem t) => CanViewTask(a, t);

    /// <summary>Attaching a file to a project: whoever may see it outright. A department that only shares the project (§6.2.1) sees its files read-only.</summary>
    public static bool CanAttachToProject(Actor a, Project p) => CanViewProject(a, p);

    /// <summary>Removing an attachment: whoever uploaded it, or anyone who may edit the task it is attached to.</summary>
    public static bool CanDeleteAttachment(Actor a, Attachment at, TaskItem parent) =>
        (a.UserId is not null && at.UploadedById == a.UserId) || CanEditTask(a, parent);

    /// <summary>Removing an attachment: whoever uploaded it, or anyone who may edit the project it is attached to.</summary>
    public static bool CanDeleteAttachment(Actor a, Attachment at, Project parent) =>
        (a.UserId is not null && at.UploadedById == a.UserId) || CanEditProject(a, parent);

    // ---------------------------------------------------------------- assets (§6.19)
    // Department = the asset's managing department. Own = the actor holds the asset (an AssetAssignment row), wherever it is
    // managed; the caller works out isAssigned from the asset's assignments. Registering an asset doesn't make it the registrar's own.

    /// <summary>Whether the actor is one of the asset's holders. Needs <see cref="Asset.Assignments"/> loaded.</summary>
    public static bool IsAssigned(Actor a, Asset asset) =>
        a.UserId is Guid me && asset.Assignments.Any(x => x.UserId == me);

    /// <summary>assets.view at Own (the actor holds it), Department (their department manages it) or All. Comments, files and activity follow it.</summary>
    public static bool CanViewAsset(Actor a, Asset asset, bool isAssigned) => a.Can(Permission.AssetsView, asset.DepartmentId, isAssigned);

    public static bool CanCreateAssetIn(Actor a, Guid departmentId) => a.CanInDepartment(Permission.AssetsCreate, departmentId);

    /// <summary>Every field, status and disposal, assignment, removing anyone's checks and files, deleting: assets.edit at Department or All - never Own.</summary>
    public static bool CanEditAsset(Actor a, Asset asset) => a.CanInDepartment(Permission.AssetsEdit, asset.DepartmentId);

    /// <summary>Moving an asset to another managing department: an edit that must also reach the target department (in practice, All).</summary>
    public static bool CanMoveAssetTo(Actor a, Guid departmentId) => a.CanInDepartment(Permission.AssetsEdit, departmentId);

    /// <summary>Recording a check: assets.check at Own (the actor holds it), Department or All. A disposed asset can't be checked.</summary>
    public static bool CanCheckAsset(Actor a, Asset asset, bool isAssigned) =>
        asset.Status != AssetStatus.Disposed && a.Can(Permission.AssetsCheck, asset.DepartmentId, isAssigned);

    /// <summary>Removing a check (for mistakes): whoever recorded it, or anyone who may edit the asset.</summary>
    public static bool CanRemoveCheck(Actor a, AssetCheck check, Asset asset) =>
        (a.UserId is not null && check.CheckedById == a.UserId) || CanEditAsset(a, asset);

    /// <summary>Commenting on an asset follows viewing it, as it does on tasks.</summary>
    public static bool CanCommentOnAsset(Actor a, Asset asset, bool isAssigned) => CanViewAsset(a, asset, isAssigned);

    /// <summary>Attaching a file to an asset follows viewing it, so a holder can upload a photo of the damage.</summary>
    public static bool CanAttachToAsset(Actor a, Asset asset, bool isAssigned) => CanViewAsset(a, asset, isAssigned);

    /// <summary>Removing an attachment: whoever uploaded it, or anyone who may edit the asset it is attached to.</summary>
    public static bool CanDeleteAttachment(Actor a, Attachment at, Asset parent) =>
        (a.UserId is not null && at.UploadedById == a.UserId) || CanEditAsset(a, parent);

    /// <summary>A department's asset types and locations: assets.configure reaching that department.</summary>
    public static bool CanConfigureAssetsIn(Actor a, Guid departmentId) => a.CanInDepartment(Permission.AssetsConfigure, departmentId);

    /// <summary>An asset's type is always one of its managing department's types - a rule, not a permission.</summary>
    public static bool CanUseAssetType(Guid assetDepartmentId, AssetType type) => type.DepartmentId == assetDepartmentId;

    /// <summary>An asset's location, when set, is always one of its managing department's locations.</summary>
    public static bool CanUseAssetLocation(Guid assetDepartmentId, AssetLocation location) => location.DepartmentId == assetDepartmentId;

    public static bool CanManageUsers(Actor a) => a.CanAnywhere(Permission.UsersManage);
    public static bool CanManageRoles(Actor a) => a.CanAnywhere(Permission.RolesManage);
    public static bool CanManageApiKeys(Actor a) => a.CanAnywhere(Permission.ApiKeysManage);
    public static bool CanManageDepartments(Actor a) => a.CanAnywhere(Permission.DepartmentsManage);
    public static bool CanManageDirectory(Actor a) => a.CanAnywhere(Permission.DirectoryManage);
    public static bool CanManageAgents(Actor a) => a.CanAnywhere(Permission.AgentsManage);

    /// <summary>reports.view at Department (the actor's own department only) or All.</summary>
    public static bool CanViewReports(Actor a) => a.ScopeOf(Permission.ReportsView) >= PermissionScope.Department;

    /// <summary>audit.view at Department (the actor's own department's entries) or All.</summary>
    public static bool CanViewAuditLog(Actor a) => a.ScopeOf(Permission.AuditView) >= PermissionScope.Department;

    public static void Require(bool allowed, string message)
    {
        if (!allowed) throw new ForbiddenException(message);
    }
}

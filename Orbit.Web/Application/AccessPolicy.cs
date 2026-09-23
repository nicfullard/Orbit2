using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The §6.5 capability table as code. Every service consults these rules, so the Razor Pages UI,
/// the MCP tools and any future REST surface are bound by exactly the same checks.
/// </summary>
public static class AccessPolicy
{
    public static bool CanViewTask(Actor a, TaskItem t) => a.CanAccessDepartment(t.DepartmentId);

    /// <summary>Member: own/assigned tasks in own department. Department Admin: any in own department. System Admin: any.</summary>
    public static bool CanEditTask(Actor a, TaskItem t) =>
        a.IsSystemAdmin ||
        (a.DepartmentId == t.DepartmentId &&
         (a.IsDepartmentAdmin || t.AssigneeId == a.UserId || t.CreatedById == a.UserId));

    /// <summary>
    /// Taking an unassigned task: anyone in the task's department may assign an open, unassigned task to themselves
    /// (System Admin: anywhere). This is the one assignee change a Member may make on a task they can't otherwise edit;
    /// once it is theirs, <see cref="CanEditTask"/> applies like any assigned task.
    /// </summary>
    public static bool CanTakeTask(Actor a, TaskItem t) =>
        a.UserId is not null && t.IsOpen && t.AssigneeId is null && a.CanAccessDepartment(t.DepartmentId);

    /// <summary>Anyone in the department may move between Todo/InProgress/Waiting/Blocked; closing or reopening needs an admin.</summary>
    public static bool CanChangeStatus(Actor a, TaskItem t, TaskItemStatus to)
    {
        if (!a.CanAccessDepartment(t.DepartmentId)) return false;
        var touchesClosed = to.IsClosed() || t.Status.IsClosed();
        return !touchesClosed || a.IsAdminFor(t.DepartmentId);
    }

    public static bool CanCloseTasks(Actor a, Guid departmentId) => a.IsAdminFor(departmentId);

    /// <summary>Backlog &lt;-&gt; sprint moves: own department's tasks (System Admin: any).</summary>
    public static bool CanPlanTask(Actor a, TaskItem t) => a.CanAccessDepartment(t.DepartmentId);

    /// <summary>Filing a task (or recurring definition) under a project: the project's own department, or a System Admin anywhere.</summary>
    public static bool CanAddTaskToProject(Actor a, Project p) => a.CanAccessDepartment(p.DepartmentId);

    /// <summary>
    /// A cross-department project task is one whose department differs from its project's (§6.2.1).
    /// Only a System Admin may introduce that pairing; the task's own department then works it like any other task of theirs.
    /// </summary>
    public static bool CanFileCrossDepartmentTask(Actor a) => a.IsSystemAdmin;

    /// <summary>
    /// The project's own department (or a System Admin) sees it outright. A department that has tasks filed under
    /// the project (§6.2.1) sees it too, read-only; pass <paramref name="hasTasksInActorDepartment"/> for that case.
    /// </summary>
    public static bool CanViewProject(Actor a, Project p, bool hasTasksInActorDepartment = false) =>
        a.CanAccessDepartment(p.DepartmentId) || hasTasksInActorDepartment;

    /// <summary>Member: projects they own. Department Admin: any in own department. System Admin: any.</summary>
    public static bool CanEditProject(Actor a, Project p) =>
        a.IsSystemAdmin ||
        (a.DepartmentId == p.DepartmentId && (a.IsDepartmentAdmin || p.OwnerId == a.UserId));

    public static bool CanManageSprints(Actor a) => a.IsSystemAdmin;

    /// <summary>Running a critical path analysis (§6.17) records a result on the project, so it follows edit rights; seeing one follows view rights.</summary>
    public static bool CanRunCriticalPath(Actor a, Project p) => CanEditProject(a, p);

    /// <summary>The working calendar is organisation-wide (§6.17).</summary>
    public static bool CanManageWorkingCalendar(Actor a) => a.IsSystemAdmin;

    public static bool CanEditRecurring(Actor a, RecurringTaskDefinition d) =>
        a.IsSystemAdmin ||
        (a.DepartmentId == d.DepartmentId &&
         (a.IsDepartmentAdmin || d.CreatedById == a.UserId || d.AssigneeId == a.UserId));

    /// <summary>Assignees log their own time; Department Admins for anyone in their department; System Admins for anyone.</summary>
    public static bool CanLogTimeFor(Actor a, TaskItem t, Guid targetUserId) =>
        a.IsSystemAdmin ||
        (a.IsDepartmentAdmin && a.DepartmentId == t.DepartmentId) ||
        (targetUserId == a.UserId && t.AssigneeId == a.UserId);

    public static bool CanEditTimeEntry(Actor a, TimeEntry e, TaskItem task) =>
        a.IsSystemAdmin ||
        (a.IsDepartmentAdmin && a.DepartmentId == task.DepartmentId) ||
        e.UserId == a.UserId;

    /// <summary>Attaching a file to a task (§6.18) follows the commenting rule: anyone who can see the task.</summary>
    public static bool CanAttachToTask(Actor a, TaskItem t) => CanViewTask(a, t);

    /// <summary>Attaching a file to a project: its own department, or a System Admin. A department that only shares the project (§6.2.1) sees its files read-only.</summary>
    public static bool CanAttachToProject(Actor a, Project p) => a.CanAccessDepartment(p.DepartmentId);

    /// <summary>Removing an attachment: whoever uploaded it, or anyone who may edit the task it is attached to.</summary>
    public static bool CanDeleteAttachment(Actor a, Attachment at, TaskItem parent) =>
        (a.UserId is not null && at.UploadedById == a.UserId) || CanEditTask(a, parent);

    /// <summary>Removing an attachment: whoever uploaded it, or anyone who may edit the project it is attached to.</summary>
    public static bool CanDeleteAttachment(Actor a, Attachment at, Project parent) =>
        (a.UserId is not null && at.UploadedById == a.UserId) || CanEditProject(a, parent);

    public static bool CanManageUsers(Actor a) => a.IsSystemAdmin;
    public static bool CanManageDepartments(Actor a) => a.IsSystemAdmin;
    public static bool CanViewReports(Actor a) => a.IsSystemAdmin;

    public static void Require(bool allowed, string message)
    {
        if (!allowed) throw new ForbiddenException(message);
    }
}

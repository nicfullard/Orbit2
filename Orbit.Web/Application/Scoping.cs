using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Applies an actor's view scope (spec §6.5) to a query, so every list is filtered server-side the same way:
/// All = no filter, Department = the actor's own department, Own = the actor's own objects, None = nothing.
/// Works on database queries and on in-memory sequences alike.
/// </summary>
public static class Scoping
{
    /// <summary>Tasks the actor may see (<c>tasks.view</c>). Own = assigned to or created by the actor.</summary>
    public static IQueryable<TaskItem> Tasks(IQueryable<TaskItem> q, Actor actor)
    {
        var dept = actor.DepartmentId;
        var me = actor.UserId;
        return actor.ScopeOf(Permission.TasksView) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(t => t.DepartmentId == dept),
            PermissionScope.Own => me is null ? q.Where(t => false) : q.Where(t => t.AssigneeId == me || t.CreatedById == me),
            _ => q.Where(t => false)
        };
    }

    /// <summary>Recurring definitions follow the task rule (<c>tasks.view</c>). Own = assigned to or created by the actor.</summary>
    public static IQueryable<RecurringTaskDefinition> RecurringDefinitions(IQueryable<RecurringTaskDefinition> q, Actor actor)
    {
        var dept = actor.DepartmentId;
        var me = actor.UserId;
        return actor.ScopeOf(Permission.TasksView) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(r => r.DepartmentId == dept),
            PermissionScope.Own => me is null ? q.Where(r => false) : q.Where(r => r.AssigneeId == me || r.CreatedById == me),
            _ => q.Where(r => false)
        };
    }

    /// <summary>
    /// Projects the actor may see (<c>projects.view</c>). Own = projects the actor owns. At Department scope,
    /// <paramref name="includeShared"/> adds other departments' projects that have tasks in the actor's department (§6.2.1, read-only).
    /// </summary>
    public static IQueryable<Project> Projects(IQueryable<Project> q, Actor actor, bool includeShared = true)
    {
        var dept = actor.DepartmentId;
        var me = actor.UserId;
        return actor.ScopeOf(Permission.ProjectsView) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => includeShared
                ? q.Where(p => p.DepartmentId == dept || p.Tasks.Any(t => t.DepartmentId == dept))
                : q.Where(p => p.DepartmentId == dept),
            PermissionScope.Own => me is null ? q.Where(p => false) : q.Where(p => p.OwnerId == me),
            _ => q.Where(p => false)
        };
    }

    /// <summary>Audit entries the actor may read (<c>audit.view</c>): All, or the actor's own department's.</summary>
    public static IQueryable<AuditLog> Audit(IQueryable<AuditLog> q, Actor actor)
    {
        var dept = actor.DepartmentId;
        return actor.ScopeOf(Permission.AuditView) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(a => a.DepartmentId == dept),
            _ => q.Where(a => false)
        };
    }
}

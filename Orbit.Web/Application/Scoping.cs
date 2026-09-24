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

    /// <summary>
    /// Assets the actor may see (<c>assets.view</c>, §6.19) - or reach with another asset permission, e.g. <c>assets.check</c> for the
    /// dashboard's checks card. Own = the assets they hold. Department = the assets their department manages plus the ones they hold
    /// elsewhere: unlike tasks, holding an asset another department manages is the normal case.
    /// </summary>
    public static IQueryable<Asset> Assets(IQueryable<Asset> q, Actor actor, string permission = Permission.AssetsView)
    {
        var dept = actor.DepartmentId;
        var me = actor.UserId;
        return actor.ScopeOf(permission) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => me is null
                ? q.Where(a => a.DepartmentId == dept)
                : q.Where(a => a.DepartmentId == dept || a.Assignments.Any(x => x.UserId == me)),
            PermissionScope.Own => me is null ? q.Where(a => false) : q.Where(a => a.Assignments.Any(x => x.UserId == me)),
            _ => q.Where(a => false)
        };
    }

    /// <summary>Asset types the actor may manage (<c>assets.configure</c>): their department's, or every department's at All.</summary>
    public static IQueryable<AssetType> AssetTypes(IQueryable<AssetType> q, Actor actor)
    {
        var dept = actor.DepartmentId;
        return actor.ScopeOf(Permission.AssetsConfigure) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(t => t.DepartmentId == dept),
            _ => q.Where(t => false)
        };
    }

    /// <summary>Asset locations the actor may manage (<c>assets.configure</c>): their department's, or every department's at All.</summary>
    public static IQueryable<AssetLocation> AssetLocations(IQueryable<AssetLocation> q, Actor actor)
    {
        var dept = actor.DepartmentId;
        return actor.ScopeOf(Permission.AssetsConfigure) switch
        {
            PermissionScope.All => q,
            PermissionScope.Department => q.Where(l => l.DepartmentId == dept),
            _ => q.Where(l => false)
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

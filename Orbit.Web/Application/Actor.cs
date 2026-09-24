using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>Claim types Orbit adds on top of the standard Identity claims.</summary>
public static class OrbitClaims
{
    public const string DepartmentId = "orbit:department_id";
    public const string DisplayName = "orbit:display_name";
    public const string ApiKeyId = "orbit:api_key_id";
    public const string ActorType = "orbit:actor_type";
    public const string AuthSource = "orbit:auth_source";
    /// <summary>Set by the API-key scheme: the id of the role the key acts with, resolved to grants per request.</summary>
    public const string RoleId = "orbit:role_id";
    /// <summary>Set only by the Orbit Agent scheme. An agent principal carries no role or department.</summary>
    public const string AgentId = "orbit:agent_id";
}

/// <summary>The role an actor acts with, as far as anything outside the role admin needs to know (spec §6.5).</summary>
/// <param name="ReachesEverywhere">Built in, or holding at least one grant at All - what the role badge colours on.</param>
public sealed record RoleRef(Guid Id, string Name, bool IsBuiltIn, bool ReachesEverywhere)
{
    /// <summary>A signed-in user or key with no role row: no grants, sees nothing.</summary>
    public static readonly RoleRef None = new(Guid.Empty, "No role", false, false);

    /// <summary>The system itself (background jobs).</summary>
    public static readonly RoleRef System = new(Guid.Empty, "System", true, true);
}

/// <summary>
/// Who is performing an operation: a signed-in user, an API key (acting as the synthetic Claude user),
/// or the system itself (background jobs), with the grants of their role. Services use this for every
/// authorization decision, so the same rules apply regardless of the entry point.
/// </summary>
public sealed record Actor(
    Guid? UserId,
    string DisplayName,
    RoleRef Role,
    IReadOnlyDictionary<string, PermissionScope> Permissions,
    Guid? DepartmentId,
    ActorType Type,
    Guid ActorId)
{
    /// <summary>The scope the actor holds a permission at; <see cref="PermissionScope.None"/> when not granted.</summary>
    public PermissionScope ScopeOf(string permission) => Permissions.GetValueOrDefault(permission);

    /// <summary>Granted at any scope: enough to open a page, not to touch a particular object.</summary>
    public bool Has(string permission) => ScopeOf(permission) > PermissionScope.None;

    /// <summary>Granted for every department.</summary>
    public bool CanAnywhere(string permission) => ScopeOf(permission) == PermissionScope.All;

    /// <summary>Granted for every department, or for the actor's own department when that is the one asked about.</summary>
    public bool CanInDepartment(string permission, Guid departmentId) =>
        ScopeOf(permission) == PermissionScope.All ||
        (ScopeOf(permission) == PermissionScope.Department && DepartmentId == departmentId);

    /// <summary>The object is in <paramref name="departmentId"/>; <paramref name="isOwn"/> says whether it is the actor's own (assigned, created or owned by them).</summary>
    public bool Can(string permission, Guid departmentId, bool isOwn) =>
        CanInDepartment(permission, departmentId) || (isOwn && ScopeOf(permission) >= PermissionScope.Own);

    /// <summary>Any grant at Department scope means the actor only makes sense inside a department.</summary>
    public bool RequiresDepartment => Permissions.Values.Any(s => s == PermissionScope.Department);

    public static readonly Actor System =
        new(null, "System", RoleRef.System, PermissionCatalog.AllAtScopeAll, null, ActorType.System, Guid.Empty);
}

public interface IActorProvider
{
    /// <summary>Resolves the current actor. Throws <see cref="ForbiddenException"/> when nobody is signed in.</summary>
    Task<Actor> GetAsync(CancellationToken ct = default);
}

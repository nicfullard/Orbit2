using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>Claim types Orbit adds on top of the standard Identity claims.</summary>
public static class OrbitClaims
{
    public const string DepartmentId = "orbit:department_id";
    public const string DisplayName = "orbit:display_name";
    public const string ApiKeyId = "orbit:api_key_id";
    public const string ActorType = "orbit:actor_type";
}

/// <summary>
/// Who is performing an operation: a signed-in user, an API key (acting as the synthetic Claude user),
/// or the system itself (background jobs). Services use this for every authorization decision,
/// so the same rules apply regardless of the entry point.
/// </summary>
public sealed record Actor(
    Guid? UserId,
    string DisplayName,
    OrbitRole Role,
    Guid? DepartmentId,
    ActorType Type,
    Guid ActorId)
{
    public bool IsSystemAdmin => Role == OrbitRole.SystemAdmin;
    public bool IsDepartmentAdmin => Role == OrbitRole.DepartmentAdmin;
    public bool IsMember => Role == OrbitRole.Member;

    /// <summary>System Admin anywhere, or Department Admin within their own department.</summary>
    public bool IsAdminFor(Guid departmentId) =>
        IsSystemAdmin || (IsDepartmentAdmin && DepartmentId == departmentId);

    /// <summary>System Admin anywhere; everyone else only within their own department.</summary>
    public bool CanAccessDepartment(Guid departmentId) =>
        IsSystemAdmin || DepartmentId == departmentId;

    public static readonly Actor System =
        new(null, "System", OrbitRole.SystemAdmin, null, ActorType.System, Guid.Empty);
}

public interface IActorProvider
{
    /// <summary>Resolves the current actor. Throws <see cref="ForbiddenException"/> when nobody is signed in.</summary>
    Task<Actor> GetAsync(CancellationToken ct = default);
}

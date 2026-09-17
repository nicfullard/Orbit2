using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class UserInput
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public OrbitRole Role { get; set; } = OrbitRole.Member;
    public AuthSource AuthSource { get; set; } = AuthSource.Local;
    /// <summary>Temporary password (create only, local users only).</summary>
    public string? Password { get; set; }
}

public sealed record UserSummary(
    Guid Id,
    string DisplayName,
    string Email,
    OrbitRole Role,
    Guid? DepartmentId,
    string? DepartmentName,
    bool IsActive,
    bool IsSystemAccount,
    DateTime CreatedAt,
    AuthSource AuthSource = AuthSource.Local,
    DateTimeOffset? LockoutEnd = null)
{
    /// <summary>Locked by too many wrong passwords. (A deactivated user is also "locked", forever - that isn't this.)</summary>
    public bool IsLockedOut => IsActive && LockoutEnd > DateTimeOffset.UtcNow;
}

public sealed record CreatedApiKey(ApiKey Key, string RawKey);

public sealed class ApiKeyInput
{
    public string Name { get; set; } = string.Empty;
    public OrbitRole Role { get; set; } = OrbitRole.Member;
    public Guid? DepartmentId { get; set; }
}

public sealed record DepartmentOverview(Department Department, int UserCount, int ActiveProjectCount, int OpenTaskCount);

public sealed record DepartmentDetail(
    Department Department,
    IReadOnlyList<UserSummary> Users,
    IReadOnlyList<ProjectListItem> Projects,
    int OpenTaskCount,
    int TotalTaskCount);

namespace Orbit.Data.Entities;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>SHA-256 hex of the raw key. The raw key is only shown once at creation.</summary>
    public string HashedKey { get; set; } = string.Empty;
    /// <summary>First few characters of the raw key, for identification in lists.</summary>
    public string Prefix { get; set; } = string.Empty;
    /// <summary>The role the key acts with (spec §6.5, §8): its grants apply to every tool call exactly as to a user in that role.</summary>
    public Guid RoleId { get; set; }
    public ApplicationRole Role { get; set; } = null!;
    /// <summary>Required when the role has any grant at Department scope; otherwise the key's default department for new work.</summary>
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;
}

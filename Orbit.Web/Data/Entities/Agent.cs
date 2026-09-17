namespace Orbit.Data.Entities;

/// <summary>
/// An on-premises Orbit Agent: a small service inside the corporate network that connects out to Orbit and
/// carries out commands Orbit can't run itself, such as checking a password against Active Directory.
/// Not to be confused with the synthetic "Claude" user that API keys act as.
/// </summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AgentStatus Status { get; set; } = AgentStatus.Pending;

    /// <summary>SHA-256 hex of the one-time registration token. Cleared once redeemed.</summary>
    public string? RegistrationTokenHash { get; set; }
    public DateTime? RegistrationExpiresAt { get; set; }

    /// <summary>SHA-256 hex of the agent's long-lived secret. The raw value exists only on the agent machine.</summary>
    public string? HashedSecret { get; set; }
    /// <summary>First few characters of the secret, for identification in lists.</summary>
    public string? SecretPrefix { get; set; }

    // Reported by the agent itself, for the admin's benefit only. Nothing is authorised on the strength of these.
    public string? MachineName { get; set; }
    public string? OsDescription { get; set; }
    public string? Version { get; set; }

    public DateTime? LastConnectedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastIpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public DateTime? RegisteredAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

using Orbit.Agents.Contracts;
using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>An agent plus its one-time registration token. The raw token is shown once and never stored.</summary>
public sealed record CreatedAgent(Agent Agent, string RawRegistrationToken);

public sealed record AgentListItem(Agent Agent, bool IsOnline);

/// <summary>Editable directory settings. The bind password is write-only: it never travels back to the page.</summary>
public sealed class LdapSettingsInput
{
    public bool Enabled { get; set; }
    public string? Server { get; set; }
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    public bool ValidateCertificate { get; set; } = true;
    public string? BindDn { get; set; }
    /// <summary>Null or empty keeps the stored password.</summary>
    public string? NewBindPassword { get; set; }
    public string? SearchBase { get; set; }
    public string? UserFilter { get; set; }
}

public sealed record LdapSettingsView(LdapSettings Settings, bool HasBindPassword);

public enum DirectoryAuthStatus
{
    Success,
    /// <summary>Wrong password, unknown user, disabled account... indistinguishable to the person signing in.</summary>
    InvalidCredentials,
    /// <summary>No agent online, the agent timed out, or the directory couldn't be reached. Not counted as a failed attempt.</summary>
    Unavailable
}

public sealed record DirectoryAuthOutcome(DirectoryAuthStatus Status, LdapAuthResult? Result = null)
{
    public bool Succeeded => Status == DirectoryAuthStatus.Success;
}

/// <summary>Result of "Test connection". <see cref="Result"/> is null when no agent could be asked at all.</summary>
public sealed record DirectoryTestOutcome(string? AgentName, LdapTestResult? Result, string? Error);

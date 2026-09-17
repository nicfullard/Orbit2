using System.Text.Json.Serialization;

namespace Orbit.Agents.Contracts;

/// <summary>
/// Directory settings, managed in Orbit (Admin &gt; Directory) and sent with every command.
/// The agent stores none of this, so a change in Orbit takes effect on the very next sign-in.
/// </summary>
public sealed class LdapConnectionSettings
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    /// <summary>False accepts any server certificate (self-signed internal CA the agent machine doesn't trust).</summary>
    public bool ValidateCertificate { get; set; } = true;
    /// <summary>Service account used to look the user up.</summary>
    public string BindDn { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
    /// <summary>LDAP filter with <c>{0}</c> where the (escaped) sign-in name goes, e.g. <c>(mail={0})</c>.</summary>
    public string UserFilter { get; set; } = "(mail={0})";
}

public sealed class LdapAuthRequest
{
    public LdapConnectionSettings Settings { get; set; } = new();
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<LdapAuthStatus>))]
public enum LdapAuthStatus
{
    /// <summary>Deliberately the zero value: a result that fails to deserialize must never read as a success.</summary>
    Error,
    Success,
    /// <summary>The directory rejected the password (or the account is disabled, locked or expired).</summary>
    InvalidCredentials,
    UserNotFound,
    /// <summary>More than one entry matched the filter; refusing to guess which one is signing in.</summary>
    Ambiguous,
    /// <summary>The directory couldn't be reached or the service-account bind failed. Not the user's fault.</summary>
    Unavailable
}

public sealed class LdapAuthResult
{
    public LdapAuthStatus Status { get; set; }
    public string? Dn { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    /// <summary>Diagnostic for Orbit's log (e.g. "account disabled"). Never shown to the person signing in.</summary>
    public string? Detail { get; set; }
}

public sealed class LdapTestRequest
{
    public LdapConnectionSettings Settings { get; set; } = new();
    /// <summary>Optional sign-in name to look up, proving the search base and filter work. No password involved.</summary>
    public string? SampleUsername { get; set; }
}

public sealed class LdapTestResult
{
    public bool Success { get; set; }
    /// <summary>What was attempted, in order, ending at the step that failed.</summary>
    public List<string> Steps { get; set; } = [];
    public string? FoundDn { get; set; }
    public string? FoundDisplayName { get; set; }
}

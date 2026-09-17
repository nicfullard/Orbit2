namespace Orbit.Data.Entities;

/// <summary>
/// Directory sign-in settings: a single row (<see cref="WellKnownIds.LdapSettingsId"/>), edited under
/// Admin &gt; Directory. Agents hold none of this; it is sent with each command.
/// </summary>
public class LdapSettings
{
    public Guid Id { get; set; } = WellKnownIds.LdapSettingsId;
    public bool Enabled { get; set; }
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    public bool ValidateCertificate { get; set; } = true;
    public string BindDn { get; set; } = string.Empty;
    /// <summary>The service-account password, encrypted with the Data Protection key ring. Never stored in the clear.</summary>
    public string? BindPasswordProtected { get; set; }
    public string SearchBase { get; set; } = string.Empty;
    /// <summary>LDAP filter with <c>{0}</c> standing for the sign-in email.</summary>
    public string UserFilter { get; set; } = DefaultUserFilter;
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }

    public const string DefaultUserFilter = "(mail={0})";
}

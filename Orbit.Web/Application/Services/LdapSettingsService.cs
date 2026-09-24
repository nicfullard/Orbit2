using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Directory sign-in settings (spec §6.13). They live only in Orbit: agents store nothing and are handed the
/// settings with each command. The service-account password is encrypted with the Data Protection key ring and
/// is write-only from the UI's point of view.
/// </summary>
public sealed class LdapSettingsService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    IDataProtectionProvider dataProtection,
    ILogger<LdapSettingsService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Orbit.Ldap.BindPassword.v1");

    public async Task<LdapSettingsView> GetAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var settings = await LoadAsync(ct) ?? new LdapSettings();
        return new LdapSettingsView(settings, !string.IsNullOrEmpty(settings.BindPasswordProtected));
    }

    /// <summary>Whether directory sign-in is switched on. Needs no actor: the login page asks before anyone is signed in.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await db.LdapSettings.AsNoTracking().AnyAsync(s => s.Id == WellKnownIds.LdapSettingsId && s.Enabled, ct);

    public async Task<LdapSettingsView> UpdateAsync(LdapSettingsInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var settings = await db.LdapSettings.FirstOrDefaultAsync(s => s.Id == WellKnownIds.LdapSettingsId, ct);
        var isNew = settings is null;
        settings ??= new LdapSettings();

        var server = input.Server?.Trim() ?? string.Empty;
        var bindDn = input.BindDn?.Trim() ?? string.Empty;
        var searchBase = input.SearchBase?.Trim() ?? string.Empty;
        var filter = string.IsNullOrWhiteSpace(input.UserFilter) ? LdapSettings.DefaultUserFilter : input.UserFilter.Trim();
        var hasPassword = !string.IsNullOrEmpty(input.NewBindPassword) || !string.IsNullOrEmpty(settings.BindPasswordProtected);
        Validate(input.Enabled, server, input.Port, bindDn, searchBase, filter, hasPassword);

        var changes = new ChangeSet()
            .Track("enabled", settings.Enabled, input.Enabled)
            .TrackText("server", settings.Server, server)
            .Track("port", settings.Port, input.Port)
            .Track("useSsl", settings.UseSsl, input.UseSsl)
            .Track("validateCertificate", settings.ValidateCertificate, input.ValidateCertificate)
            .TrackText("bindDn", settings.BindDn, bindDn)
            .TrackText("searchBase", settings.SearchBase, searchBase)
            .TrackText("userFilter", settings.UserFilter, filter);
        // The audit trail records that the password changed, never what it changed to.
        var details = new Dictionary<string, object?>(changes.Changes);
        if (!string.IsNullOrEmpty(input.NewBindPassword)) details["bindPassword"] = "changed";
        if (details.Count == 0) return new LdapSettingsView(settings, hasPassword);

        settings.Enabled = input.Enabled;
        settings.Server = server;
        settings.Port = input.Port;
        settings.UseSsl = input.UseSsl;
        settings.ValidateCertificate = input.ValidateCertificate;
        settings.BindDn = bindDn;
        settings.SearchBase = searchBase;
        settings.UserFilter = filter;
        if (!string.IsNullOrEmpty(input.NewBindPassword))
            settings.BindPasswordProtected = _protector.Protect(input.NewBindPassword);
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedById = actor.UserId;
        if (isNew) db.LdapSettings.Add(settings);

        audit.Add(actor, AuditEntity.LdapSettings, settings.Id, isNew ? AuditAction.Created : AuditAction.Updated, null, "Directory sign-in settings", details);
        await db.SaveChangesAsync(ct);
        return new LdapSettingsView(settings, !string.IsNullOrEmpty(settings.BindPasswordProtected));
    }

    /// <summary>
    /// The settings to send with a sign-in command, or null when directory sign-in is off or can't work
    /// (nothing saved yet, or the stored password can no longer be decrypted). No actor: this runs during login.
    /// </summary>
    public async Task<LdapConnectionSettings?> GetForDispatchAsync(CancellationToken ct = default)
    {
        var settings = await LoadAsync(ct);
        if (settings is null || !settings.Enabled) return null;
        var password = Unprotect(settings.BindPasswordProtected);
        return password is null ? null : ToContract(settings, password);
    }

    /// <summary>
    /// "Test connection" works on what is typed in the form, so settings can be proven before they're saved.
    /// A blank password field means "the one already stored".
    /// </summary>
    public async Task<LdapConnectionSettings> BuildForTestAsync(LdapSettingsInput input, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var stored = await LoadAsync(ct);
        var password = !string.IsNullOrEmpty(input.NewBindPassword) ? input.NewBindPassword : Unprotect(stored?.BindPasswordProtected);
        var server = input.Server?.Trim() ?? string.Empty;
        var bindDn = input.BindDn?.Trim() ?? string.Empty;
        var searchBase = input.SearchBase?.Trim() ?? string.Empty;
        var filter = string.IsNullOrWhiteSpace(input.UserFilter) ? LdapSettings.DefaultUserFilter : input.UserFilter.Trim();
        Validate(true, server, input.Port, bindDn, searchBase, filter, !string.IsNullOrEmpty(password));
        return new LdapConnectionSettings
        {
            Server = server, Port = input.Port, UseSsl = input.UseSsl, ValidateCertificate = input.ValidateCertificate,
            BindDn = bindDn, BindPassword = password!, SearchBase = searchBase, UserFilter = filter
        };
    }

    private static void Validate(bool enabled, string server, int port, string bindDn, string searchBase, string filter, bool hasPassword)
    {
        if (port is < 1 or > 65535) throw new ValidationException("Port must be between 1 and 65535.");
        if (!filter.Contains("{0}", StringComparison.Ordinal) || !filter.StartsWith('(') || !filter.EndsWith(')'))
            throw new ValidationException("The user filter must be an LDAP filter in parentheses containing {0} where the sign-in email goes, e.g. (mail={0}).");
        // Half-finished settings can be saved while directory sign-in is off; switching it on needs all of them.
        if (!enabled) return;
        if (server.Length == 0) throw new ValidationException("A server is required.");
        if (bindDn.Length == 0) throw new ValidationException("A bind DN (service account) is required.");
        if (!hasPassword) throw new ValidationException("A bind password is required.");
        if (searchBase.Length == 0) throw new ValidationException("A search base is required.");
    }

    private Task<LdapSettings?> LoadAsync(CancellationToken ct) =>
        db.LdapSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == WellKnownIds.LdapSettingsId, ct);

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch (CryptographicException)
        {
            // The Data Protection key ring was lost or replaced. Re-entering the password under Admin > Directory fixes it.
            logger.LogError("The stored LDAP bind password can't be decrypted with the current Data Protection key ring. Re-enter it under Admin > Directory.");
            return null;
        }
    }

    private static LdapConnectionSettings ToContract(LdapSettings s, string bindPassword) => new()
    {
        Server = s.Server, Port = s.Port, UseSsl = s.UseSsl, ValidateCertificate = s.ValidateCertificate,
        BindDn = s.BindDn, BindPassword = bindPassword, SearchBase = s.SearchBase, UserFilter = s.UserFilter
    };

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageDirectory(actor), "You don't have permission to manage directory sign-in.");
        return actor;
    }
}

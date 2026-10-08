using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Orbit.Application.Services;

/// <summary>
/// The Nagios passwords at rest (§6.21, §9): encrypted with the Data Protection key ring, as the directory's bind password is.
/// Shared by the settings pages, which write them, and the monitor, which reads them for each check.
/// </summary>
public sealed class NagiosPasswordProtector(IDataProtectionProvider dataProtection, ILogger<NagiosPasswordProtector> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Orbit.Nagios.Password.v1");

    public string Protect(string password) => _protector.Protect(password);

    /// <summary>Null when nothing is stored, or when it can no longer be read.</summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch (CryptographicException)
        {
            // The Data Protection key ring was lost or replaced. Re-entering the password under Admin > Nagios fixes it.
            logger.LogError("A stored Nagios password can't be decrypted with the current Data Protection key ring. Re-enter it under Admin > Nagios.");
            return null;
        }
    }
}

using System.Text.RegularExpressions;
using Novell.Directory.Ldap;
using Orbit.Agents.Contracts;

namespace Orbit.Agent.Ldap;

/// <summary>
/// Checks a username and password against the directory: bind as the service account, find the one entry the
/// sign-in name matches, then bind as that entry with the password supplied. The settings come with each request;
/// nothing is cached and the password is never logged.
/// </summary>
public sealed partial class LdapDirectory(ILogger<LdapDirectory> logger)
{
    private static readonly string[] UserAttributes = ["displayName", "mail", "userPrincipalName"];
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(12);
    private const int ConnectTimeoutMs = 8000;

    public async Task<LdapAuthResult> AuthenticateAsync(LdapAuthRequest request, CancellationToken stopping)
    {
        var settings = request.Settings;
        var username = request.Username?.Trim() ?? string.Empty;
        if (settings is null || string.IsNullOrWhiteSpace(settings.Server))
            return Result(LdapAuthStatus.Error, "no directory settings were supplied");
        if (username.Length == 0)
            return Result(LdapAuthStatus.InvalidCredentials, "empty username");
        // A simple bind with an empty password is an "unauthenticated bind": the server reports success without
        // checking anything. It must never be attempted on someone's behalf.
        if (string.IsNullOrEmpty(request.Password))
            return Result(LdapAuthStatus.InvalidCredentials, "empty password refused");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        budget.CancelAfter(OperationBudget);
        var ct = budget.Token;

        LdapEntry entry;
        try
        {
            using var search = await ConnectAsync(settings, ct);
            await search.BindAsync(settings.BindDn, settings.BindPassword, ct);
            var matches = await FindAsync(search, settings, username, ct);
            if (matches.Count == 0) return Log(username, Result(LdapAuthStatus.UserNotFound, "no directory entry matches the user filter"));
            if (matches.Count > 1) return Log(username, Result(LdapAuthStatus.Ambiguous, "more than one directory entry matches the user filter"));
            entry = matches[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            // Anything wrong before the user's own bind is a problem with the directory, the network or the service
            // account - not with what the user typed - so it must not count against them.
            return Log(username, Result(LdapAuthStatus.Unavailable, "lookup failed: " + Describe(ex)));
        }

        if (string.IsNullOrEmpty(entry.Dn))
            return Log(username, Result(LdapAuthStatus.Error, "the matching entry has no DN"));

        try
        {
            using var userConnection = await ConnectAsync(settings, ct);
            await userConnection.BindAsync(entry.Dn, request.Password, ct);
            if (!userConnection.Bound)
                return Log(username, Result(LdapAuthStatus.InvalidCredentials, "bind did not authenticate"));
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
        {
            return Log(username, Result(LdapAuthStatus.InvalidCredentials, DescribeBindFailure(ex)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            return Log(username, Result(LdapAuthStatus.Unavailable, "bind failed: " + Describe(ex)));
        }

        return Log(username, new LdapAuthResult
        {
            Status = LdapAuthStatus.Success,
            Dn = entry.Dn,
            DisplayName = entry.GetStringValueOrDefault("displayName", null),
            Email = entry.GetStringValueOrDefault("mail", null) ?? entry.GetStringValueOrDefault("userPrincipalName", null)
        });
    }

    /// <summary>"Test connection" from Orbit's admin page. Reports each step so a failure says where it happened.</summary>
    public async Task<LdapTestResult> TestAsync(LdapTestRequest request, CancellationToken stopping)
    {
        var result = new LdapTestResult();
        var settings = request.Settings;
        if (settings is null || string.IsNullOrWhiteSpace(settings.Server))
        {
            result.Steps.Add("Failed: no directory settings were supplied.");
            return result;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        budget.CancelAfter(OperationBudget);
        var ct = budget.Token;
        var step = $"connect to {settings.Server}:{settings.Port}";
        try
        {
            using var connection = await ConnectAsync(settings, ct);
            result.Steps.Add($"Connected to {settings.Server}:{settings.Port} from {Environment.MachineName} ({Transport(settings)}).");

            step = "bind as the service account";
            await connection.BindAsync(settings.BindDn, settings.BindPassword, ct);
            if (!connection.Bound) throw new InvalidOperationException("the server accepted the bind without authenticating it - check the bind DN and password");
            result.Steps.Add($"Bound as {settings.BindDn}.");

            var sample = request.SampleUsername?.Trim();
            if (!string.IsNullOrEmpty(sample))
            {
                step = "search for the sample user";
                var matches = await FindAsync(connection, settings, sample, ct);
                if (matches.Count != 1)
                {
                    result.Steps.Add(matches.Count == 0
                        ? $"Failed: nothing under \"{settings.SearchBase}\" matches {LdapFilter.ForUser(settings.UserFilter, sample)}."
                        : $"Failed: more than one entry matches {LdapFilter.ForUser(settings.UserFilter, sample)}. Sign-in needs exactly one; tighten the filter.");
                    return result;
                }
                result.FoundDn = matches[0].Dn;
                result.FoundDisplayName = matches[0].GetStringValueOrDefault("displayName", null);
                result.Steps.Add($"Found {result.FoundDn}{(result.FoundDisplayName is null ? "" : $" ({result.FoundDisplayName})")} for {sample}.");
            }
            result.Success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            result.Steps.Add($"Failed to {step}: {Describe(ex)}");
        }
        return result;
    }

    private static async Task<LdapConnection> ConnectAsync(LdapConnectionSettings settings, CancellationToken ct)
    {
        var options = new LdapConnectionOptions();
        if (settings.UseSsl) options.UseSsl();
        // Validation stays on unless the admin switched it off in Orbit: accepting any certificate would let anything
        // on the network path pose as the directory and collect passwords.
        if (!settings.ValidateCertificate)
            options.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);

        var connection = new LdapConnection(options) { ConnectionTimeout = ConnectTimeoutMs };
        try
        {
            await connection.ConnectAsync(settings.Server, settings.Port, ct);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Entries matching the user filter - at most two, which is enough to know whether the match is unique.</summary>
    private static async Task<List<LdapEntry>> FindAsync(LdapConnection connection, LdapConnectionSettings settings, string username, CancellationToken ct)
    {
        var filter = LdapFilter.ForUser(settings.UserFilter, username);
        var results = await connection.SearchAsync(settings.SearchBase ?? string.Empty, LdapConnection.ScopeSub, filter, UserAttributes, false, ct);
        var entries = new List<LdapEntry>(2);
        while (entries.Count < 2 && await results.HasMoreAsync(ct))
        {
            try
            {
                entries.Add(await results.NextAsync(ct));
            }
            catch (LdapReferralException)
            {
                // Active Directory answers a search from the domain root with referrals to other partitions
                // (DomainDnsZones and so on). They aren't matches and aren't followed.
            }
        }
        return entries;
    }

    private LdapAuthResult Log(string username, LdapAuthResult result)
    {
        if (result.Status == LdapAuthStatus.Success)
            logger.LogInformation("Directory sign-in OK for {Username} ({Dn}).", username, result.Dn);
        else
            logger.LogWarning("Directory sign-in {Status} for {Username}: {Detail}", result.Status, username, result.Detail);
        return result;
    }

    private static LdapAuthResult Result(LdapAuthStatus status, string detail) => new() { Status = status, Detail = detail };

    private static string Transport(LdapConnectionSettings s) =>
        !s.UseSsl ? "no TLS - passwords cross the network in the clear"
        : s.ValidateCertificate ? "LDAPS, certificate validated"
        : "LDAPS, certificate NOT validated";

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "timed out",
        LdapException { ResultCode: LdapException.InvalidCredentials } => "the service account's bind DN or password was rejected",
        LdapException l => $"LDAP error {l.ResultCode}: {Clean(l.LdapErrorMessage ?? l.Message)}",
        _ => Clean(ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})")
    };

    /// <summary>
    /// Active Directory says why a bind failed in a sub-code ("... AcceptSecurityContext error, data 52e, ..."). Worth
    /// recording for the admin; the person signing in is only ever told "invalid login attempt".
    /// </summary>
    private static string DescribeBindFailure(LdapException ex)
    {
        var match = AdSubCode().Match(ex.LdapErrorMessage ?? string.Empty);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() switch
        {
            "525" => "user not found (AD 525)",
            "52e" => "wrong password (AD 52e)",
            "530" => "not permitted to log on at this time (AD 530)",
            "531" => "not permitted to log on from this machine (AD 531)",
            "532" => "password expired (AD 532)",
            "533" => "account disabled (AD 533)",
            "701" => "account expired (AD 701)",
            "773" => "user must change password at next logon (AD 773)",
            "775" => "account locked out (AD 775)",
            var code => $"bind rejected (AD {code})"
        } : "wrong password or account not usable";
    }

    private static string Clean(string message) => message.Replace('\0', ' ').Trim();

    [GeneratedRegex(@"data\s+([0-9a-fA-F]{3,4})\b")]
    private static partial Regex AdSubCode();
}

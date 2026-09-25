using System.Text.RegularExpressions;
using Novell.Directory.Ldap;
using Novell.Directory.Ldap.Controls;
using Orbit.Agents.Contracts;

namespace Orbit.Agent.Ldap;

/// <summary>
/// Checks a username and password against the directory: bind as the service account, find the one entry the
/// sign-in name matches, then bind as that entry with the password supplied. The settings come with each request;
/// nothing is cached and the password is never logged. It also lists user entries for Orbit's directory import.
/// </summary>
public sealed partial class LdapDirectory(ILogger<LdapDirectory> logger)
{
    private static readonly string[] UserAttributes = ["displayName", "mail", "userPrincipalName"];
    private static readonly string[] ListAttributes =
        ["objectGUID", "displayName", "givenName", "sn", "mail", "userPrincipalName", "sAMAccountName", "title", "userAccountControl"];
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(12);
    private const int ConnectTimeoutMs = 8000;
    private const int PageSize = 500;
    private const string PagedResultsOid = "1.2.840.113556.1.4.319";
    private const int AccountDisabled = 0x2;

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

    /// <summary>
    /// "Import from directory" in Orbit: every entry the admin's filter matches, read a page at a time - Active Directory
    /// hands at most 1,000 entries to a search without paging. Only the count is logged, never the entries.
    /// </summary>
    public async Task<LdapListUsersResult> ListUsersAsync(LdapListUsersRequest request, CancellationToken stopping)
    {
        var result = new LdapListUsersResult();
        var settings = request.Settings;
        if (settings is null || string.IsNullOrWhiteSpace(settings.Server))
            return Failed(result, "no directory settings were supplied");
        var filter = request.Filter?.Trim() ?? string.Empty;
        if (!filter.StartsWith('(') || !filter.EndsWith(')'))
            return Failed(result, "the filter must be an LDAP filter in parentheses");
        var departmentAttribute = string.IsNullOrWhiteSpace(request.DepartmentAttribute) ? "department" : request.DepartmentAttribute.Trim();
        if (!AttributeName().IsMatch(departmentAttribute))
            return Failed(result, $"\"{departmentAttribute}\" is not an attribute name");
        var searchBase = string.IsNullOrWhiteSpace(request.SearchBase) ? settings.SearchBase ?? string.Empty : request.SearchBase.Trim();
        var max = Math.Clamp(request.MaxResults, 1, 50_000);
        var seconds = Math.Clamp(request.TimeLimitSeconds, 5, 600);

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        var ct = budget.Token;
        string[] attributes = [.. ListAttributes, departmentAttribute];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = $"connect to {settings.Server}:{settings.Port}";
        try
        {
            using var connection = await ConnectAsync(settings, ct);
            step = "bind as the service account";
            await connection.BindAsync(settings.BindDn, settings.BindPassword, ct);
            if (!connection.Bound)
                return Failed(result, "the server accepted the bind without authenticating it - check the bind DN and password");

            step = $"search \"{searchBase}\"";
            var cookie = SimplePagedResultsControl.GetEmptyCookie;
            do
            {
                // The library doesn't watch the cancellation token while it waits for the server, so the budget is also
                // each page's own time limits: the client stops waiting, and the server stops working, when it runs out.
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new TimeoutException();
                var constraints = new LdapSearchConstraints
                {
                    MaxResults = 0, // the default stops at 1,000 with an error; the page size bounds each round trip
                    TimeLimit = (int)left.TotalMilliseconds,
                    ServerTimeLimit = Math.Max(1, (int)left.TotalSeconds),
                    ReferralFollowing = false
                };
                constraints.SetControls(new SimplePagedResultsControl(PageSize, cookie));
                var page = await connection.SearchAsync(searchBase, LdapConnection.ScopeSub, filter, attributes, false, constraints, ct);
                while (await page.HasMoreAsync(ct))
                {
                    LdapEntry entry;
                    try
                    {
                        entry = await page.NextAsync(ct);
                    }
                    catch (LdapReferralException)
                    {
                        continue; // other partitions under the domain root, as in FindAsync
                    }
                    if (result.Users.Count >= max)
                    {
                        result.Truncated = true;
                        break;
                    }
                    var user = ToDirectoryUser(entry, departmentAttribute);
                    if (seen.Add(user.Id)) result.Users.Add(user);
                }
                if (result.Truncated) break;
                cookie = NextCookie(page.ResponseControls);
            } while (cookie.Length > 0);
            result.Success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            var read = result.Users.Count;
            result.Users.Clear();
            var timedOut = ex is OperationCanceledException or TimeoutException
                or LdapException { ResultCode: LdapException.LdapTimeout or LdapException.TimeLimitExceeded };
            return Failed(result, timedOut
                ? $"timed out after {seconds} seconds trying to {step}, with {read} entries read. Narrow the filter or the search base"
                : $"failed to {step}: {Describe(ex)}");
        }

        logger.LogInformation("Listed {Count} directory entries under {SearchBase}{Truncated}.",
            result.Users.Count, searchBase, result.Truncated ? " (stopped at the limit)" : "");
        return result;
    }

    private LdapListUsersResult Failed(LdapListUsersResult result, string error)
    {
        logger.LogWarning("Directory listing failed: {Error}", error);
        result.Success = false;
        result.Error = error;
        return result;
    }

    /// <summary>The paged-results control comes back on the last message of each page; an empty cookie means that was the last page.</summary>
    private static byte[] NextCookie(LdapControl[]? controls)
    {
        var control = controls?.FirstOrDefault(c => c.Id == PagedResultsOid);
        return control switch
        {
            null => [],
            SimplePagedResultsControl paged => paged.Cookie ?? [],
            _ => new SimplePagedResultsControl(control.Id, control.Critical, control.GetValue()).Cookie ?? []
        };
    }

    private static LdapDirectoryUser ToDirectoryUser(LdapEntry entry, string departmentAttribute)
    {
        var guid = entry.GetBytesValueOrDefault("objectGUID", null);
        var control = Text(entry, "userAccountControl");
        return new LdapDirectoryUser
        {
            // objectGUID is 16 bytes in the same order .NET's Guid(byte[]) reads, so this is the form AD's own tools show.
            Id = guid is { Length: 16 } ? new Guid(guid).ToString() : entry.Dn,
            Dn = entry.Dn,
            DisplayName = Text(entry, "displayName"),
            GivenName = Text(entry, "givenName"),
            Surname = Text(entry, "sn"),
            Mail = Text(entry, "mail"),
            UserPrincipalName = Text(entry, "userPrincipalName"),
            SamAccountName = Text(entry, "sAMAccountName"),
            Title = Text(entry, "title"),
            Department = Text(entry, departmentAttribute),
            Disabled = int.TryParse(control, out var flags) && (flags & AccountDisabled) != 0
        };
    }

    /// <summary>A single-valued text attribute. Attribute names are case-insensitive in LDAP, and the admin may type any case.</summary>
    private static string? Text(LdapEntry entry, string name)
    {
        var attribute = entry.GetOrDefault(name, null)
            ?? entry.GetAttributeSet().FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var value = attribute?.StringValue;
        return string.IsNullOrWhiteSpace(value) ? null : Clean(value);
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

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeName();
}

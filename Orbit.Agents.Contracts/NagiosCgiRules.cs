namespace Orbit.Agents.Contracts;

/// <summary>
/// Where a Nagios Core instance's CGIs live and what Orbit may ask of them (spec §6.21, §8.3). Shared so Orbit checks an address
/// with the very rules the agent enforces. The agent only ever fetches <c>statusjson.cgi</c>, a read-only report: nothing Orbit
/// sends can reach <c>cmd.cgi</c>, which is the one that changes things in Nagios.
/// </summary>
public static class NagiosCgiRules
{
    /// <summary>The only CGI the agent calls.</summary>
    public const string StatusCgi = "statusjson.cgi";
    public const int MaxUrlLength = 500;
    public const int MaxQueryLength = 500;
    public const int MaxQueries = 12;

    /// <summary>
    /// The CGI directory for the address an admin typed. Nagios Core serves its CGIs from <c>cgi-bin/</c> under the web address
    /// (<c>http://host/nagios/</c> gives <c>http://host/nagios/cgi-bin/</c>); an address that already names a <c>cgi-bin</c>
    /// folder - or a CGI inside one - is taken as it stands, which covers layouts such as <c>/cgi-bin/nagios4/</c>.
    /// </summary>
    public static bool TryCgiBase(string? url, out Uri cgiBase, out string error)
    {
        cgiBase = null!;
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0) return Fail("A Nagios address is required.", out error);
        if (text.Length > MaxUrlLength) return Fail($"The Nagios address is too long (at most {MaxUrlLength} characters).", out error);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Fail("The Nagios address must start with http:// or https://, e.g. http://nagios.example/nagios/.", out error);
        if (uri.UserInfo.Length > 0)
            return Fail("Leave the username and password out of the Nagios address; they have their own fields.", out error);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return Fail("The Nagios address must not contain a query (?) or fragment (#).", out error);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[^1].EndsWith(".cgi", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(segments.Count - 1);
        if (!segments.Any(s => s.Equals("cgi-bin", StringComparison.OrdinalIgnoreCase))) segments.Add("cgi-bin");
        cgiBase = new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/" + string.Join('/', segments) + "/").Uri;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The address of one status query. The query string is used exactly as given (a plus sign separates the states in a
    /// filter and must not be re-encoded), so only the characters a statusjson.cgi query needs are let through.
    /// </summary>
    public static bool TryStatusUrl(Uri cgiBase, string? query, out Uri url, out string error)
    {
        url = null!;
        var q = query ?? string.Empty;
        if (!q.StartsWith("query=", StringComparison.Ordinal) || q.Length > MaxQueryLength || !q.All(IsQueryCharacter))
            return Fail("That is not a Nagios status query.", out error);
        url = new Uri(cgiBase, $"{StatusCgi}?{q}");
        error = string.Empty;
        return true;
    }

    /// <summary>The Nagios page about a host or one of its services, for a person to open. Never fetched by the agent.</summary>
    public static string ExtInfoUrl(Uri cgiBase, string host, string? service) =>
        string.IsNullOrEmpty(service)
            ? new Uri(cgiBase, $"extinfo.cgi?type=1&host={Uri.EscapeDataString(host)}").AbsoluteUri
            : new Uri(cgiBase, $"extinfo.cgi?type=2&host={Uri.EscapeDataString(host)}&service={Uri.EscapeDataString(service)}").AbsoluteUri;

    private static bool IsQueryCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '=' or '&' or '+' or '%' or '.' or '_' or '-' or '~';

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

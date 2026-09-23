using Microsoft.AspNetCore.Http.Extensions;

namespace Orbit.Helpers;

/// <summary>
/// Remembers a list page's filters for the browser session (spec §6.2): the last filter used is kept in a session cookie
/// (one per page, tied to the signed-in user, HttpOnly) and reapplied - by redirect - when the page is opened with no
/// filter of its own. Nothing is stored server-side, and the address bar always shows the filters in force, so a
/// filtered list stays shareable and the browser's back button behaves.
/// </summary>
public static class FilterMemory
{
    private const string CookiePrefix = "orbit.filters.";

    /// <summary>True when the request names any of the page's filter fields itself - even empty, as a submitted form with everything on "All" does.</summary>
    public static bool IsExplicit(HttpRequest request, IEnumerable<string> filterKeys) =>
        filterKeys.Any(request.Query.ContainsKey);

    /// <summary>The remembered query string for this page and user ("?a=1&amp;b=2"), or null when there is none or it belongs to someone else.</summary>
    public static string? Recall(HttpRequest request, string page, Guid? userId)
    {
        if (!request.Cookies.TryGetValue(CookiePrefix + page, out var raw) || string.IsNullOrEmpty(raw)) return null;
        var split = raw.IndexOf('|');
        if (split < 0 || raw[..split] != Owner(userId)) return null;
        var query = raw[(split + 1)..];
        return query.Length == 0 ? null : "?" + query;
    }

    /// <summary>Remember the filter fields present in this request (non-empty values only). An all-empty filter forgets the cookie instead.</summary>
    public static void Remember(HttpRequest request, HttpResponse response, string page, Guid? userId, IEnumerable<string> filterKeys)
    {
        var qb = new QueryBuilder();
        foreach (var key in filterKeys)
            foreach (var value in request.Query[key])
                if (!string.IsNullOrWhiteSpace(value)) qb.Add(key, value);
        var query = qb.ToQueryString().Value?.TrimStart('?') ?? string.Empty;
        if (query.Length == 0)
        {
            Forget(response, page);
            return;
        }
        // No Expires: a session cookie, gone when the browser closes. Secure is applied by the cookie policy outside development.
        // The value (an already URL-encoded query) is cookie-encoded by ASP.NET on the way out and decoded on the way in.
        response.Cookies.Append(CookiePrefix + page, Owner(userId) + "|" + query,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, Path = "/" });
    }

    public static void Forget(HttpResponse response, string page) =>
        response.Cookies.Delete(CookiePrefix + page, new CookieOptions { Path = "/" });

    private static string Owner(Guid? userId) => userId?.ToString("N") ?? "-";
}

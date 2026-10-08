using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Orbit.Agents.Contracts;

namespace Orbit.Agent.Nagios;

/// <summary>
/// Reads a Nagios Core instance's status for Orbit (<see cref="AgentMethods.QueryNagios"/>, spec §6.21): Orbit sends the address,
/// the sign-in and the queries, this fetches them and hands the JSON back untouched - Orbit does the reading, so a change to what
/// it looks for needs no new agent. Only <c>statusjson.cgi</c> is ever called (<see cref="NagiosCgiRules"/>), which reports and
/// changes nothing. Never throws: a failure goes back as the result, worded for the admin.
/// </summary>
public sealed class NagiosClient(ILogger<NagiosClient> logger)
{
    /// <summary>Everything one command may return, kept well under the hub's message limit once the JSON is escaped inside JSON.</summary>
    private const int MaxTotalBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task<NagiosQueryResult> QueryAsync(NagiosQueryRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        if (!NagiosCgiRules.TryCgiBase(request.BaseUrl, out var cgiBase, out var error)) return Fail(null, error, started);
        if (request.Queries.Count is 0 or > NagiosCgiRules.MaxQueries) return Fail(cgiBase, "Orbit asked for an unexpected number of Nagios queries.", started);
        var urls = new List<Uri>();
        foreach (var query in request.Queries)
        {
            if (!NagiosCgiRules.TryStatusUrl(cgiBase, query, out var url, out error)) return Fail(cgiBase, error, started);
            urls.Add(url);
        }

        var seconds = Math.Clamp(request.TimeLimitSeconds, 5, 300);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        // Nagios is inside the network: no proxy (the one in the environment is for reaching Orbit, and would be handed the
        // sign-in), and no following a redirect to somewhere else with it.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = ConnectTimeout };
        // Validation stays on unless the admin switched it off in Orbit: accepting any certificate would let anything on the
        // network path pose as Nagios and collect the password.
        if (!request.ValidateCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}")));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var bodies = new List<string>(urls.Count);
        var total = 0;
        try
        {
            foreach (var url in urls)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return Fail(cgiBase, "Nagios rejected the username or password (HTTP 401).", started);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return Fail(cgiBase, "Nagios refused this user (HTTP 403).", started);
                if (!response.IsSuccessStatusCode)
                    return Fail(cgiBase, $"Nagios answered HTTP {(int)response.StatusCode} at {url.GetLeftPart(UriPartial.Path)}. Check the address: it is the one you open Nagios with, e.g. http://nagios.example/nagios/.", started);

                var bytes = await ReadAsync(response, MaxTotalBytes - total, timeout.Token);
                if (bytes is null)
                    return Fail(cgiBase, $"Nagios sent more than {MaxTotalBytes / (1024 * 1024)} MB, which is more than Orbit takes in one check.", started);
                total += bytes.Length;
                if (!IsJson(bytes))
                    return Fail(cgiBase, $"{url.GetLeftPart(UriPartial.Path)} did not answer with JSON. Check the address: it is the one you open Nagios with, e.g. http://nagios.example/nagios/.", started);
                bodies.Add(Encoding.UTF8.GetString(bytes));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(cgiBase, $"Nagios did not answer within {seconds} seconds.", started);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(cgiBase, Explain(ex, cgiBase), started);
        }

        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("Read Nagios status from {Host} ({Queries} queries, {Bytes} bytes, {Ms} ms).", cgiBase.Authority, urls.Count, total, ms);
        return new NagiosQueryResult { Ok = true, Bodies = bodies, DurationMs = ms };
    }

    /// <summary>The body, or null when it is larger than what is left of the allowance.</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, int allowance, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > allowance) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > allowance) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>A sign-in page or an error page is HTML: only a JSON document goes back to Orbit.</summary>
    private static bool IsJson(byte[] bytes)
    {
        try
        {
            using var _ = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Explain(Exception ex, Uri cgiBase) => ex is HttpRequestException http
        ? http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"This agent's machine can't resolve the name {cgiBase.Host}.",
            HttpRequestError.ConnectionError => $"This agent couldn't connect to {cgiBase.Authority}: {Innermost(ex)}",
            HttpRequestError.SecureConnectionError => $"The secure connection to {cgiBase.Authority} failed: {Innermost(ex)} If Nagios uses a certificate this machine doesn't trust, install its CA here or switch off certificate validation for this instance in Orbit.",
            _ => $"The request to {cgiBase.Authority} failed: {Innermost(ex)}"
        }
        : $"The request to {cgiBase.Authority} failed: {Innermost(ex)}";

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message.TrimEnd('.') + ".";
    }

    private NagiosQueryResult Fail(Uri? cgiBase, string error, long started)
    {
        logger.LogWarning("Reading Nagios status from {Host} failed: {Error}", cgiBase?.Authority ?? "(no address)", error);
        return new NagiosQueryResult { Ok = false, Error = error, DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    }
}

using System.Text.Json;
using Orbit.Data.Entities;

namespace Orbit.Application.Nagios;

/// <summary>
/// Reads the JSON Nagios Core's <c>statusjson.cgi</c> answers with (§6.21) into a <see cref="NagiosSnapshot"/>. Pure, so it is
/// unit-tested against captured answers. It refuses rather than guesses: the CGI answers HTTP 200 to a query it rejected, and an
/// answer read as "no problems" when it was really an error would resolve every open incident.
/// </summary>
public static class NagiosStatusParser
{
    // statusjson.cgi's status values: bit flags, one per state.
    private const int Pending = 1, HostUp = 2, HostDown = 4, HostUnreachable = 8;
    private const int ServiceOk = 2, ServiceWarning = 4, ServiceUnknown = 8, ServiceCritical = 16;
    private const int HardState = 1;
    private const int MaxOutputLength = 1000;

    /// <param name="bodies">The answers to <see cref="NagiosQueries.Check"/>, in that order, optionally followed by the program status.</param>
    public static bool TryParse(IReadOnlyList<string> bodies, out NagiosSnapshot snapshot, out string error)
    {
        snapshot = null!;
        if (bodies.Count < NagiosQueries.Check.Count)
            return Fail($"Nagios answered {bodies.Count} of {NagiosQueries.Check.Count} queries.", out error);

        var documents = new List<JsonDocument>();
        try
        {
            for (var i = 0; i < bodies.Count; i++)
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(bodies[i]); }
                catch (JsonException) { return Fail($"Nagios' answer to \"{Name(i)}\" is not JSON.", out error); }
                documents.Add(document);
                if (Refusal(document.RootElement, Name(i)) is { } refused) return Fail(refused, out error);
            }

            var first = documents[0].RootElement.GetProperty("result");
            if (Time(first, "query_time") is not DateTime queryTime) return Fail("Nagios' answer has no query time.", out error);

            if (List(documents[0], "hostlist") is not JsonElement hostList) return Fail(Missing(0), out error);
            if (List(documents[1], "servicelist") is not JsonElement serviceList) return Fail(Missing(1), out error);
            if (List(documents[2], "hostlist") is not JsonElement hostProblems) return Fail(Missing(2), out error);
            if (List(documents[3], "servicelist") is not JsonElement serviceProblems) return Fail(Missing(3), out error);
            if (List(documents[4], "downtimelist") is not JsonElement downtimes) return Fail(Missing(4), out error);

            var hosts = new Dictionary<string, NagiosObjectStatus>(StringComparer.Ordinal);
            foreach (var host in hostList.EnumerateObject())
                hosts[host.Name] = HostStatus(Number(host.Value));
            // A Nagios with no hosts watches nothing. Far likelier, the user Orbit signs in as may no longer see them - and reading
            // that as "everything is gone" would close every incident.
            if (hosts.Count == 0)
                return Fail("Nagios listed no hosts. Check that the user Orbit signs in as is authorised to see all hosts and services (cgi.cfg).", out error);

            var services = new Dictionary<(string, string), NagiosObjectStatus>();
            foreach (var host in serviceList.EnumerateObject())
            {
                if (host.Value.ValueKind != JsonValueKind.Object) return Fail(Missing(1), out error);
                foreach (var service in host.Value.EnumerateObject())
                    services[(host.Name, service.Name)] = ServiceStatus(Number(service.Value));
            }

            var problems = new List<NagiosProblem>();
            foreach (var host in hostProblems.EnumerateObject())
            {
                if (host.Value.ValueKind != JsonValueKind.Object) return Fail(Missing(2), out error);
                if (HostState(Number(host.Value, "status")) is NagiosProblemState state)
                    problems.Add(Problem(host.Name, string.Empty, state, host.Value, "last_time_up", queryTime));
            }
            foreach (var host in serviceProblems.EnumerateObject())
            {
                if (host.Value.ValueKind != JsonValueKind.Object) return Fail(Missing(3), out error);
                foreach (var service in host.Value.EnumerateObject())
                {
                    if (service.Value.ValueKind != JsonValueKind.Object) return Fail(Missing(3), out error);
                    if (ServiceState(Number(service.Value, "status")) is NagiosProblemState state)
                        problems.Add(Problem(host.Name, service.Name, state, service.Value, "last_time_ok", queryTime));
                }
            }

            var hostsInDowntime = new HashSet<string>(StringComparer.Ordinal);
            foreach (var downtime in downtimes.EnumerateObject())
            {
                var d = downtime.Value;
                if (d.ValueKind != JsonValueKind.Object || !Flag(d, "is_in_effect")) continue;
                // A service's downtime names the service; one that doesn't is the host's, and covers everything on it.
                if (Text(d, "host_name") is { Length: > 0 } hostName && string.IsNullOrEmpty(Text(d, "service_description")))
                    hostsInDowntime.Add(hostName);
            }

            string? version = null;
            if (documents.Count > NagiosQueries.Check.Count
                && documents[^1].RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("programstatus", out var program)
                && program.ValueKind == JsonValueKind.Object)
                version = Text(program, "version");

            snapshot = new NagiosSnapshot(queryTime, Time(first, "last_data_update"), hosts, services, problems, hostsInDowntime, version);
            error = string.Empty;
            return true;
        }
        finally
        {
            foreach (var document in documents) document.Dispose();
        }
    }

    /// <summary>
    /// Why this reading can't be trusted, or null: Nagios stopped writing its status data longer ago than <paramref name="staleAfterSeconds"/>,
    /// so every "down since" in it is a statement about the past. Both times are Nagios' own, so clocks that disagree don't matter.
    /// </summary>
    public static string? Staleness(NagiosSnapshot snapshot, int staleAfterSeconds)
    {
        if (snapshot.LastDataUpdate is not DateTime updated) return null;
        var age = snapshot.QueryTime - updated;
        return age <= TimeSpan.FromSeconds(Math.Max(30, staleAfterSeconds))
            ? null
            : $"Nagios has not updated its status for {(int)age.TotalMinutes} minutes (last at {updated:yyyy-MM-dd HH:mm} UTC). The Nagios service may have stopped.";
    }

    private static NagiosProblem Problem(string host, string service, NagiosProblemState state, JsonElement e, string lastOkField, DateTime queryTime)
    {
        var hard = Number(e, "state_type") == HardState;
        // While Nagios is still rechecking, its last hard change is the one before this problem - when it was last settled as well.
        var since = (hard ? Time(e, "last_hard_state_change") : null) ?? Time(e, "last_state_change") ?? queryTime;
        return new NagiosProblem(
            host, service, state,
            Hard: hard,
            // A clock that is ahead on the Nagios host must not make a problem look like it starts in the future.
            Since: since > queryTime ? queryTime : since,
            LastOkAt: Time(e, lastOkField),
            Acknowledged: Flag(e, "problem_has_been_acknowledged"),
            InDowntime: Number(e, "scheduled_downtime_depth") > 0,
            Output: NagiosText.Clean(Text(e, "plugin_output"), MaxOutputLength));
    }

    /// <summary>statusjson.cgi reports a rejected query in the body, with HTTP 200.</summary>
    private static string? Refusal(JsonElement root, string query)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return $"Nagios' answer to \"{query}\" is not a status report.";
        if (Number(result, "type_code", -1) == 0) return null;
        var said = string.Join(": ", new[] { Text(result, "type_text"), Text(result, "message") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return $"Nagios refused \"{query}\"{(said.Length == 0 ? "." : $": {NagiosText.Clean(said, 300)}")}";
    }

    private static JsonElement? List(JsonDocument document, string name) =>
        document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Object
            ? list
            : null;

    private static string Name(int index) => index switch
    {
        0 => "host list", 1 => "service list", 2 => "host problems", 3 => "service problems", 4 => "downtimes", _ => "program status"
    };

    private static string Missing(int index) => $"Nagios' answer to \"{Name(index)}\" has no list in it.";

    private static NagiosObjectStatus HostStatus(int code) => code switch
    {
        HostUp => NagiosObjectStatus.Ok,
        HostDown or HostUnreachable => NagiosObjectStatus.Problem,
        _ => NagiosObjectStatus.Pending
    };

    private static NagiosObjectStatus ServiceStatus(int code) => code switch
    {
        ServiceOk => NagiosObjectStatus.Ok,
        ServiceWarning or ServiceUnknown or ServiceCritical => NagiosObjectStatus.Problem,
        _ => NagiosObjectStatus.Pending
    };

    private static NagiosProblemState? HostState(int code) => code switch
    {
        HostDown => NagiosProblemState.Down,
        HostUnreachable => NagiosProblemState.Unreachable,
        _ => null
    };

    private static NagiosProblemState? ServiceState(int code) => code switch
    {
        ServiceWarning => NagiosProblemState.Warning,
        ServiceUnknown => NagiosProblemState.Unknown,
        ServiceCritical => NagiosProblemState.Critical,
        _ => null
    };

    private static int Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : Pending;

    private static int Number(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : fallback;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Nagios writes times as milliseconds since 1970, and 0 for "never".</summary>
    private static DateTime? Time(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var ms) || ms <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

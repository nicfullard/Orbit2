using System.Text;
using Orbit.Data.Entities;

namespace Orbit.Application.Nagios;

/// <summary>
/// The words Orbit writes about a Nagios problem (§6.21): the raised task's title and description, and the notes added to it
/// afterwards. Pure. Everything Nagios supplied is cleaned first - a plugin's output is arbitrary text, and one control
/// character the database refuses would otherwise fail every check of that instance.
/// </summary>
public static class NagiosText
{
    public const int MaxTitleLength = 300;
    private const int MaxOutputInText = 1000;

    /// <summary>Control characters out (line breaks and tabs become spaces), trimmed, clipped; null when nothing is left.</summary>
    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var cleaned = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '\t') cleaned.Append(' ');
            else if (!char.IsControl(c) && (!char.IsSurrogate(c) || IsPaired(text, i))) cleaned.Append(c);
        }
        var result = cleaned.ToString().Trim();
        if (result.Length == 0) return null;
        if (result.Length <= maxLength) return result;
        var cut = Math.Max(0, maxLength - 3);
        if (cut > 0 && char.IsHighSurrogate(result[cut - 1])) cut--;
        return result[..cut].TrimEnd() + "...";
    }

    public static string TaskTitle(NagiosProblem problem)
    {
        var title = problem.IsService
            ? $"Nagios: {problem.Service} on {problem.Host} is {problem.State.Label()}"
            : $"Nagios: {problem.Host} is {problem.State.Label()}";
        return Clean(title, MaxTitleLength) ?? "Nagios problem";
    }

    /// <param name="link">The Nagios page about the host or service, when the instance's address can be turned into one.</param>
    public static string TaskDescription(string instanceName, NagiosProblem problem, string? link)
    {
        var text = new StringBuilder();
        text.Append("Raised by Orbit from the Nagios instance \"").Append(Clean(instanceName, 100)).Append("\".\n\n");
        text.Append("Host: ").Append(Clean(problem.Host, 255)).Append('\n');
        if (problem.IsService) text.Append("Service: ").Append(Clean(problem.Service, 255)).Append('\n');
        text.Append("State: ").Append(problem.State.Label()).Append(" since ").Append(When(problem.Since)).Append('\n');
        // A snapshot: the text can change while the problem lasts, and the task page shows the current one beside this.
        if (Clean(problem.Output, MaxOutputInText) is { } output) text.Append("When raised, Nagios said: ").Append(output).Append('\n');
        if (!string.IsNullOrEmpty(link)) text.Append('\n').Append(link).Append('\n');
        text.Append("\nOrbit adds a note here when Nagios reports it well again. It does not close the task.");
        return text.ToString();
    }

    /// <param name="taskOpen">The task is still open, so the note says why Orbit left it that way.</param>
    public static string RecoveredNote(string instanceName, string host, string service, DateTime seenAt, bool taskOpen) =>
        $"Nagios ({Clean(instanceName, 100)}) reports {Subject(host, service)} {(service.Length == 0 ? "UP" : "OK")} again, as of {When(seenAt)}."
        + (taskOpen ? " Orbit leaves the task open: close it when the cause is dealt with." : string.Empty);

    /// <summary>Nagios says the object was well at some point since the incident opened, and it is down again now.</summary>
    public static string RecoveredAndFailedAgainNote(string instanceName, string host, string service) =>
        $"Nagios ({Clean(instanceName, 100)}) reports that {Subject(host, service)} recovered and has gone down again since Orbit last looked.";

    public static string VanishedNote(string instanceName, string host, string service) =>
        $"Nagios ({Clean(instanceName, 100)}) no longer lists {Subject(host, service)}: it was removed or renamed there, or the user Orbit signs in as can no longer see it. Orbit has stopped watching it.";

    public static string DownAgainNote(string instanceName, NagiosProblem problem)
    {
        var note = $"Nagios ({Clean(instanceName, 100)}) reports {Subject(problem.Host, problem.Service)} {problem.State.Label()} again, since {When(problem.Since)}. This task was still open, so no new one was raised.";
        return Clean(problem.Output, MaxOutputInText) is { } output ? $"{note}\nNagios said: {output}" : note;
    }

    /// <summary>What a check does about a problem, for the admin reading a "Test connection" result.</summary>
    /// <param name="when">How the page writes a time; UTC, labelled, when it has no view on that.</param>
    public static string Verdict(NagiosDecision decision, Func<DateTime, string>? when = null) => decision.Verdict switch
    {
        NagiosVerdict.Raise => "A task is raised",
        NagiosVerdict.Join => "Joins the task that is still open for it",
        NagiosVerdict.Waiting => decision.RaisesAt is DateTime at ? $"Counting: a task at {(when ?? When)(at)} if it lasts" : "Counting towards its threshold",
        NagiosVerdict.HeldBack => "Past its threshold, held back by the limit on new tasks per check",
        NagiosVerdict.AlreadyRaised => "Already has a task",
        NagiosVerdict.Soft => "Nagios is still rechecking it",
        NagiosVerdict.NotSelected => "Not a state this instance raises tasks for",
        NagiosVerdict.HostDown => "Its host is not up: the host is the problem",
        NagiosVerdict.Downtime => "In scheduled downtime",
        NagiosVerdict.Acknowledged => "Acknowledged in Nagios",
        _ => decision.Verdict.ToString()
    };

    private static string Subject(string host, string service) =>
        service.Length == 0 ? $"host {Clean(host, 255)}" : $"{Clean(service, 255)} on {Clean(host, 255)}";

    private static string When(DateTime utc) => $"{utc:yyyy-MM-dd HH:mm} UTC";

    /// <summary>A surrogate with its partner is a real character (an emoji in a plugin's output); alone it is not valid text.</summary>
    private static bool IsPaired(string text, int index) =>
        char.IsHighSurrogate(text[index]) ? index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
        : index > 0 && char.IsHighSurrogate(text[index - 1]);
}

using Orbit.Agents.Contracts;
using Orbit.Application.Models;

namespace Orbit.Application.Nagios;

/// <summary>What a Nagios instance's settings must be before they are saved or tested (§6.21). Pure.</summary>
public static class NagiosInstanceRules
{
    public const int MaxNameLength = 100;
    public const int MaxUsernameLength = 100;
    public const int MaxPasswordLength = 200;
    public const int MaxCheckIntervalMinutes = 24 * 60;
    /// <summary>Thirty days: beyond that the threshold is not a threshold.</summary>
    public const int MaxThresholdMinutes = 30 * 24 * 60;
    public const int MaxNewTasksPerCheckLimit = 100;

    public static string RequireName(string? name)
    {
        var n = name?.Trim() ?? string.Empty;
        if (n.Length == 0) throw new ValidationException("A name is required.");
        if (n.Length > MaxNameLength) throw new ValidationException($"The name must be {MaxNameLength} characters or fewer.");
        return n;
    }

    /// <summary>The address as typed, trimmed, once <see cref="NagiosCgiRules"/> can make a CGI address of it.</summary>
    public static string RequireUrl(string? url)
    {
        if (!NagiosCgiRules.TryCgiBase(url, out _, out var error)) throw new ValidationException(error);
        return url!.Trim();
    }

    public static string RequireUsername(string? username)
    {
        var u = username?.Trim() ?? string.Empty;
        if (u.Length == 0) throw new ValidationException("A username is required.");
        if (u.Length > MaxUsernameLength) throw new ValidationException($"The username must be {MaxUsernameLength} characters or fewer.");
        // HTTP basic authentication joins the two with a colon, so a username can't contain one.
        if (u.Contains(':')) throw new ValidationException("The username must not contain a colon.");
        return u;
    }

    /// <summary>The connection half: what "Test connection" needs as well as a save.</summary>
    public static (string BaseUrl, string Username) RequireConnection(NagiosInstanceInput input, bool hasPassword)
    {
        var url = RequireUrl(input.BaseUrl);
        var username = RequireUsername(input.Username);
        if (input.NewPassword is { Length: > MaxPasswordLength }) throw new ValidationException($"The password must be {MaxPasswordLength} characters or fewer.");
        if (!hasPassword) throw new ValidationException("A password is required.");
        if (input.AgentId is null || input.AgentId == Guid.Empty) throw new ValidationException("Choose the Orbit Agent that can reach this Nagios instance.");
        return (url, username);
    }

    /// <summary>The thresholds and the states that raise tasks.</summary>
    public static void RequireRules(NagiosInstanceInput input)
    {
        if (input.CheckIntervalMinutes is < 1 or > MaxCheckIntervalMinutes)
            throw new ValidationException($"Check every 1 to {MaxCheckIntervalMinutes} minutes.");
        if (input.HostThresholdMinutes is < 0 or > MaxThresholdMinutes || input.ServiceThresholdMinutes is < 0 or > MaxThresholdMinutes)
            throw new ValidationException($"A threshold is between 0 and {MaxThresholdMinutes} minutes.");
        if (input.MaxNewTasksPerCheck is < 1 or > MaxNewTasksPerCheckLimit)
            throw new ValidationException($"The most new tasks in one check is between 1 and {MaxNewTasksPerCheckLimit}.");
        if (!Enum.IsDefined(input.TaskPriority)) throw new ValidationException("Choose a priority for the tasks.");
        if (!(input.RaiseHostDown || input.RaiseHostUnreachable || input.RaiseServiceCritical || input.RaiseServiceWarning || input.RaiseServiceUnknown))
            throw new ValidationException("Tick at least one host or service state that raises a task.");
    }
}

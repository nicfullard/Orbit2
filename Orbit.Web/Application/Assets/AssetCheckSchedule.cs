using Orbit.Data.Entities;

namespace Orbit.Application.Assets;

/// <summary>Where an asset stands against its check schedule (spec §6.19).</summary>
public enum CheckDueState
{
    /// <summary>No schedule: the type has no check interval, or the asset is Lost or Disposed.</summary>
    None,
    Ok,
    DueSoon,
    Overdue
}

/// <summary>
/// The check schedule (spec §6.19), as pure functions. The next check is due one interval after the last check - or after the date
/// the asset was registered, when it has never been checked - and is computed on read, never stored, so changing a type's interval
/// re-dates all of its assets at once. Only Active, InStorage and Damaged assets are scheduled.
/// </summary>
public static class AssetCheckSchedule
{
    /// <summary>The statuses that have a check schedule.</summary>
    public static readonly IReadOnlyList<AssetStatus> ScheduledStatuses = [AssetStatus.Active, AssetStatus.InStorage, AssetStatus.Damaged];

    public static bool IsScheduled(AssetStatus status) => ScheduledStatuses.Contains(status);

    /// <summary>The next check due, or null when the asset has no schedule.</summary>
    public static DateOnly? NextDue(AssetStatus status, int? intervalDays, DateOnly? lastCheckedOn, DateOnly registeredOn) =>
        intervalDays is int days && days > 0 && IsScheduled(status)
            ? (lastCheckedOn ?? registeredOn).AddDays(days)
            : null;

    public static DateOnly? NextDue(Asset asset) =>
        NextDue(asset.Status, asset.AssetType?.CheckIntervalDays, asset.LastCheckedOn, RegisteredOn(asset));

    /// <summary>The day the asset was registered in Orbit (UTC, like every date in Orbit).</summary>
    public static DateOnly RegisteredOn(Asset asset) => DateOnly.FromDateTime(asset.CreatedAt);

    /// <summary>Overdue before today; due soon from today up to <paramref name="dueSoonDays"/> ahead; otherwise Ok.</summary>
    public static CheckDueState StateOf(DateOnly? due, DateOnly today, int dueSoonDays)
    {
        if (due is not DateOnly d) return CheckDueState.None;
        if (d < today) return CheckDueState.Overdue;
        return d <= today.AddDays(Math.Max(0, dueSoonDays)) ? CheckDueState.DueSoon : CheckDueState.Ok;
    }

    /// <summary>The check that sets an asset's last check: the latest by date, ties broken by when it was recorded.</summary>
    public static AssetCheck? Latest(IEnumerable<AssetCheck> checks) =>
        checks.OrderByDescending(c => c.CheckDate).ThenByDescending(c => c.CreatedAt).FirstOrDefault();

    /// <summary>A last check that found a problem or didn't find the asset, flagged until a later check says otherwise.</summary>
    public static bool LastCheckNotOk(Asset asset) => asset.LastCheckOutcome is AssetCheckOutcome outcome && outcome != AssetCheckOutcome.Ok;
}

using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application.Assets;

/// <summary>
/// The asset rules of spec §6.19 that don't need the database, as pure functions: field limits, the asset number, disposal,
/// checks, type and location ownership. <c>AssetService</c> applies them to the UI and the MCP tools alike.
/// </summary>
public static class AssetRules
{
    public const int MaxAssetNumberLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxNotesLength = 2000;

    /// <summary>
    /// The asset number as stored: the asset's number in the ERP asset register, which not every asset has - so null when
    /// blank; otherwise trimmed, at most 50 characters. Uniqueness among the numbers that are set ignores case (the service checks it).
    /// </summary>
    public static string? NormaliseAssetNumber(string? assetNumber)
    {
        var n = assetNumber?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        if (n.Length > MaxAssetNumberLength) throw new ValidationException($"The asset number must be {MaxAssetNumberLength} characters or fewer.");
        return n;
    }

    /// <summary>Two asset numbers are the same when they match ignoring case and surrounding spaces; having none is never a clash.</summary>
    public static bool SameAssetNumber(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>How an asset is named in messages, audit summaries and page titles: its ERP number and name, or just its name.</summary>
    public static string Label(string? assetNumber, string name) =>
        string.IsNullOrWhiteSpace(assetNumber) ? name : $"{assetNumber} - {name}";

    public static string RequireName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("A name is required.");
        if (n.Length > MaxNameLength) throw new ValidationException($"The name must be {MaxNameLength} characters or fewer.");
        return n;
    }

    /// <summary>An optional text field: null when blank, trimmed, refused when longer than <paramref name="max"/>.</summary>
    public static string? Clean(string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > max) throw new ValidationException($"{label} must be {max} characters or fewer.");
        return v;
    }

    public static decimal? CleanValue(decimal? value)
    {
        if (value is null) return null;
        if (value < 0) throw new ValidationException("The purchase value can't be negative.");
        if (value >= 10_000_000_000_000_000m) throw new ValidationException("The purchase value is too large.");
        return decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    }

    public static DateOnly? CleanPurchaseDate(DateOnly? purchaseDate, DateOnly today)
    {
        if (purchaseDate is DateOnly d && d > today) throw new ValidationException("The purchase date can't be in the future.");
        return purchaseDate;
    }

    /// <summary>
    /// The disposal date an asset should hold for its status: none unless it is Disposed; when Disposed, the date given (today when
    /// none is), never in the future and never before the purchase date. Reinstating (any other status) clears it.
    /// </summary>
    public static DateOnly? DisposalDate(AssetStatus status, DateOnly? disposedOn, DateOnly? purchaseDate, DateOnly today)
    {
        if (status != AssetStatus.Disposed) return null;
        var date = disposedOn ?? today;
        if (date > today) throw new ValidationException("The disposal date can't be in the future.");
        if (purchaseDate is DateOnly bought && date < bought) throw new ValidationException("The disposal date can't be before the purchase date.");
        return date;
    }

    /// <summary>
    /// A check as it may be recorded: not on a disposed asset, not dated in the future, with notes describing the issue when one was
    /// found. Returns the cleaned notes.
    /// </summary>
    public static string? ValidateCheck(AssetStatus status, AssetCheckOutcome outcome, DateOnly checkDate, string? notes, DateOnly today)
    {
        if (status == AssetStatus.Disposed) throw new ValidationException("A disposed asset can't be checked.");
        if (checkDate > today) throw new ValidationException("A check can't be dated in the future.");
        var n = Clean(notes, MaxNotesLength, "Check notes");
        if (outcome == AssetCheckOutcome.IssueFound && n is null)
            throw new ValidationException("Describe the issue found in the notes.");
        return n;
    }

    /// <summary>A quick-check scan (§6.19): a serial number or ERP asset number, trimmed; blank is refused.</summary>
    public static string CleanScan(string? scanned) =>
        Clean(scanned, 100, "The serial number") ?? throw new ValidationException("Scan or type a serial number.");

    /// <summary>
    /// The assets a quick-check scan names (§6.19), from the matches within the caller's assets.check reach: the ones that aren't
    /// disposed, by name. One is checked at once; several are offered to choose from. None, or only disposed ones, is refused.
    /// </summary>
    public static IReadOnlyList<QuickCheckCandidate> QuickCheckTargets(IReadOnlyList<QuickCheckCandidate> matches, string scanned)
    {
        if (matches.Count == 0)
            throw new ValidationException($"No asset you can check has the serial number or asset number \"{scanned}\".");
        var live = matches.Where(m => m.Status != AssetStatus.Disposed).OrderBy(m => m.Name).ThenBy(m => m.AssetNumber).ToList();
        if (live.Count == 0)
            throw new ValidationException(matches.Count == 1
                ? $"\"{Label(matches[0].AssetNumber, matches[0].Name)}\" is disposed, so it can't be checked."
                : $"Every asset with \"{scanned}\" is disposed, so none can be checked.");
        return live;
    }

    /// <summary>
    /// Whether this person already recorded an OK check on the asset today, so a repeat quick-check scan records nothing (§6.19).
    /// Someone else's check, or one with another outcome, doesn't count: an asset not found this morning still gets its check.
    /// </summary>
    public static bool CheckedOkToday(IEnumerable<AssetCheck> checks, Guid? userId, DateOnly today) =>
        userId is Guid me && checks.Any(c => c.CheckedById == me && c.CheckDate == today && c.Outcome == AssetCheckOutcome.Ok);

    /// <summary>
    /// Why an asset can't be deleted, or null when it can: only an asset with no history - no checks, no linked tasks and no recurring
    /// task that generates tasks about it - may be; otherwise dispose of it.
    /// </summary>
    public static string? DeleteBlocker(int checkCount, int taskCount = 0, int recurringCount = 0)
    {
        var history = new List<string>();
        if (checkCount > 0) history.Add(checkCount == 1 ? "a check" : $"{checkCount} checks");
        if (taskCount > 0) history.Add(taskCount == 1 ? "a linked task" : $"{taskCount} linked tasks");
        if (recurringCount > 0) history.Add(recurringCount == 1 ? "a recurring task" : $"{recurringCount} recurring tasks");
        if (history.Count == 0) return null;
        var list = history.Count == 1 ? history[0] : $"{string.Join(", ", history.Take(history.Count - 1))} and {history[^1]}";
        return $"This asset has {list} on record, so it can't be deleted. Dispose of it instead.";
    }

    /// <summary>
    /// Whether a task or recurring task may be linked to this asset (§6.19): one the caller can see - so the picker and the MCP tools
    /// offer and accept the same assets - that isn't disposed. Only a new or changed link is checked: a link that a save keeps stays,
    /// whoever edits the task and whatever has happened to the asset since.
    /// </summary>
    public static void RequireLinkable(Asset asset, bool canView)
    {
        if (!canView) throw new ValidationException("You don't have permission to see that asset, so a task can't be linked to it.");
        if (asset.Status == AssetStatus.Disposed)
            throw new ValidationException($"\"{Label(asset.AssetNumber, asset.Name)}\" is disposed, so a task can't be linked to it.");
    }

    /// <summary>
    /// Whether what was typed or scanned into the asset picker is exactly this asset's ERP asset number or serial number, ignoring case
    /// and surrounding spaces (§6.19) - so a scan followed by Enter picks it without choosing from a list.
    /// </summary>
    public static bool MatchesScan(string? assetNumber, string? serialNumber, string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        return SameAssetNumber(assetNumber, t) || (serialNumber is not null && string.Equals(serialNumber.Trim(), t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An asset's type must be one of its managing department's types and its location, when set, one of its locations. On a move
    /// to another department both are the new department's: the type has to be chosen again, the location chosen again or cleared.
    /// </summary>
    public static void CheckTypeAndLocation(Guid departmentId, string departmentName, AssetType type, AssetLocation? location, bool moving)
    {
        if (!AccessPolicy.CanUseAssetType(departmentId, type))
            throw new ValidationException(moving
                ? $"Moving the asset to {departmentName} needs one of {departmentName}'s asset types; \"{type.Name}\" belongs to its current department."
                : $"The asset type \"{type.Name}\" belongs to another department; choose one of {departmentName}'s types.");
        if (location is not null && !AccessPolicy.CanUseAssetLocation(departmentId, location))
            throw new ValidationException(moving
                ? $"Moving the asset to {departmentName} needs one of {departmentName}'s locations, or none; \"{location.Name}\" belongs to its current department."
                : $"The location \"{location.Name}\" belongs to another department; choose one of {departmentName}'s locations.");
    }
}

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

    /// <summary>Why an asset can't be deleted, or null when it can: only an asset with no history - no checks - may be; otherwise dispose of it.</summary>
    public static string? DeleteBlocker(int checkCount) => checkCount > 0
        ? $"This asset has {(checkCount == 1 ? "a check" : $"{checkCount} checks")} on record, so it can't be deleted. Dispose of it instead."
        : null;

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

using Orbit.Application.Assets;
using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>The check filter on the Assets list and <c>list_assets</c> (spec §6.19).</summary>
public enum AssetCheckFilter
{
    Overdue,
    DueSoon,
    /// <summary>Due soon or overdue.</summary>
    Due,
    /// <summary>The last check found an issue or didn't find the asset.</summary>
    NotOk,
    Never
}

/// <summary>The warranty filter on the Assets list and <c>list_assets</c> (spec §6.19).</summary>
public enum AssetWarrantyFilter
{
    Expiring,
    Expired
}

public sealed class AssetFilter
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? AssetTypeId { get; set; }
    public string? Category { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>Blank = every status but Disposed; "all" = every status; otherwise one <see cref="AssetStatus"/> by name.</summary>
    public string? Status { get; set; }
    public bool AssignedToMe { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public bool Unassigned { get; set; }
    public bool HeldByDeactivated { get; set; }
    public AssetCheckFilter? Check { get; set; }
    public AssetWarrantyFilter? Warranty { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    public const string AllStatuses = "all";
}

/// <summary>A row of the Assets list: the asset (with its type, location, department and holders) and where it stands against its check schedule.</summary>
public sealed record AssetListItem(Asset Asset, DateOnly? NextCheckDue, CheckDueState CheckState);

/// <summary>
/// An asset as the form or an MCP call describes it (spec §6.19). <see cref="AssigneeIds"/> null leaves the holders as they are;
/// otherwise it is the whole set. <see cref="Properties"/> holds changes keyed by property id: a key present sets the value (or clears
/// it when blank), a key absent leaves it - or, on a change of type, carries it over when the new type has a namesake.
/// </summary>
public sealed class AssetInput
{
    /// <summary>The ERP asset register number, when the asset is on it; blank for none.</summary>
    public string? AssetNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>The managing department; defaults to the caller's own.</summary>
    public Guid? DepartmentId { get; set; }
    public Guid AssetTypeId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public Guid? AssetLocationId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public decimal? PurchaseValue { get; set; }
    public string? PurchaseOrder { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Supplier { get; set; }
    public DateOnly? WarrantyExpiresOn { get; set; }
    /// <summary>When the status is Disposed: the day it went (today when not given).</summary>
    public DateOnly? DisposedOn { get; set; }
    public IReadOnlyList<Guid>? AssigneeIds { get; set; }
    /// <summary>create_asset only: a retried call with the same key returns the asset already registered.</summary>
    public string? IdempotencyKey { get; set; }
    public Dictionary<Guid, string?> Properties { get; set; } = new();

    public static AssetInput From(Asset a) => new()
    {
        AssetNumber = a.AssetNumber, Name = a.Name, Description = a.Description, DepartmentId = a.DepartmentId, AssetTypeId = a.AssetTypeId,
        Manufacturer = a.Manufacturer, Model = a.Model, SerialNumber = a.SerialNumber, Status = a.Status, AssetLocationId = a.AssetLocationId,
        PurchaseDate = a.PurchaseDate, PurchaseValue = a.PurchaseValue, PurchaseOrder = a.PurchaseOrder, InvoiceNumber = a.InvoiceNumber,
        Supplier = a.Supplier, WarrantyExpiresOn = a.WarrantyExpiresOn, DisposedOn = a.DisposedOn
    };
}

public sealed class AssetCheckInput
{
    public DateOnly? CheckDate { get; set; }
    public AssetCheckOutcome Outcome { get; set; } = AssetCheckOutcome.Ok;
    public string? Notes { get; set; }
}

/// <summary>The dashboard's Asset checks card (spec §6.19): counts within the viewer's assets.check reach.</summary>
public sealed record AssetCheckSummary(PermissionScope Scope, int Overdue, int DueSoon, int NotOk, int Held);

/// <summary>
/// An asset by name: another asset that looks like the same item (same manufacturer and serial number), or the asset a quick
/// check found (spec §6.19).
/// </summary>
public sealed record AssetRef(Guid Id, string? AssetNumber, string Name);

/// <summary>
/// One match in the asset picker (§6.19): enough to tell similar assets apart - type, location, holders, status - and whether it is
/// exactly the ERP number or serial number that was typed or scanned.
/// </summary>
public sealed record AssetPickerItem(Guid Id, string? AssetNumber, string Name, string TypeName, string? LocationName,
    IReadOnlyList<string> Holders, AssetStatus Status, bool Exact);

/// <summary>
/// An asset's task history as the caller may see it (§6.19): the linked tasks within their tasks.view reach (the first few), how many
/// of those there are in all, and how many other tasks are linked that they can't see.
/// </summary>
public sealed record AssetTaskHistory(IReadOnlyList<TaskItem> Tasks, int VisibleCount, int HiddenCount);

/// <summary>An asset a quick-check scan matched, before disposed ones are set aside (spec §6.19).</summary>
public sealed record QuickCheckCandidate(Guid Id, string? AssetNumber, string Name, AssetStatus Status);

public enum QuickCheckStatus
{
    /// <summary>Today's OK check was recorded.</summary>
    Recorded,
    /// <summary>The caller had already recorded an OK check on it today, so nothing was recorded (a repeat scan).</summary>
    AlreadyChecked,
    /// <summary>The serial number is on several assets the caller can check: nothing was recorded, the caller chooses.</summary>
    ChooseAsset
}

/// <summary>A quick check's outcome: the asset checked (or already checked), or the assets to choose from.</summary>
public sealed record QuickCheckResult(QuickCheckStatus Status, IReadOnlyList<AssetRef> Assets);

/// <summary>A type or location found among the assets a viewer can see, for the list filters.</summary>
public sealed record AssetFilterOption(Guid Id, string Name, string? Group, Guid DepartmentId, string DepartmentName);

/// <summary>Suggestions for the free-text fields on the asset form (a datalist of values already in use).</summary>
public sealed record AssetSuggestions(IReadOnlyList<string> Manufacturers, IReadOnlyList<string> Models, IReadOnlyList<string> Suppliers);

public sealed class AssetTypeInput
{
    /// <summary>Set once, at creation; defaults to the caller's own department.</summary>
    public Guid? DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    /// <summary>Blank or 0 = no scheduled checks.</summary>
    public int? CheckIntervalDays { get; set; }
    /// <summary>Read only on create: the type's first properties, in display order (create_asset_type; the pages add them on the edit page).</summary>
    public IReadOnlyList<AssetPropertyInput> Properties { get; set; } = [];
}

public sealed class AssetPropertyInput
{
    public string Name { get; set; } = string.Empty;
    public AssetPropertyType PropertyType { get; set; } = AssetPropertyType.Text;
    /// <summary>A Choice property's options.</summary>
    public IReadOnlyList<string> Options { get; set; } = [];
    public bool IsRequired { get; set; }
}

/// <summary>One property change in <c>AssetTypeService.ApplyAsync</c>: a change to an existing property, or an addition when <paramref name="PropertyId"/> is null.</summary>
public sealed record AssetPropertyChange(Guid? PropertyId, AssetPropertyInput Input);

public sealed record AssetTypeListItem(AssetType Type, int PropertyCount, int AssetCount);

public sealed class AssetLocationInput
{
    /// <summary>Set once, at creation; defaults to the caller's own department.</summary>
    public Guid? DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed record AssetLocationListItem(AssetLocation Location, int AssetCount);

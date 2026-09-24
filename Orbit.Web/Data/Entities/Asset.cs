namespace Orbit.Data.Entities;

/// <summary>
/// A physical asset in the register (spec §6.19): a laptop, a vehicle, a fire extinguisher. Owned by its managing department -
/// the one that buys, issues, recovers and disposes of it - whatever the department of whoever holds it. Its type and location
/// are always the managing department's own. Separate from projects and tasks.
/// </summary>
public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The managing department: what Department scope means for assets (§6.5).</summary>
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    /// <summary>
    /// The asset's number in the ERP system's asset register, when it is on it - many assets aren't. Optional free text; unique
    /// ignoring case when set.
    /// </summary>
    public string? AssetNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Always one of the managing department's types.</summary>
    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>Not unique: a duplicate within one manufacturer is flagged, not refused.</summary>
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    /// <summary>When set, always one of the managing department's locations.</summary>
    public Guid? AssetLocationId { get; set; }
    public AssetLocation? AssetLocation { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    /// <summary>In the organisation's one currency.</summary>
    public decimal? PurchaseValue { get; set; }
    public string? PurchaseOrder { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Supplier { get; set; }
    public DateOnly? WarrantyExpiresOn { get; set; }
    /// <summary>Set exactly when <see cref="Status"/> is Disposed.</summary>
    public DateOnly? DisposedOn { get; set; }
    /// <summary>The latest check's date and outcome, maintained by AssetService from the checks - never edited directly. Null when never checked.</summary>
    public DateOnly? LastCheckedOn { get; set; }
    public AssetCheckOutcome? LastCheckOutcome { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Client-supplied key on create_asset: a retried call returns the asset already registered instead of a second one.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Who holds it: any number of people, or nobody. Being assigned is what Own means for assets.</summary>
    public ICollection<AssetAssignment> Assignments { get; set; } = new List<AssetAssignment>();
    public ICollection<AssetCheck> Checks { get; set; } = new List<AssetCheck>();
    public ICollection<AssetPropertyValue> PropertyValues { get; set; } = new List<AssetPropertyValue>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}

/// <summary>One person holding an asset (§6.19). Earlier assignments are in the asset's audit history.</summary>
public class AssetAssignment
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public Guid? AssignedById { get; set; }
    public ApplicationUser? AssignedBy { get; set; }
}

/// <summary>A dated record that someone looked at an asset (§6.19). A check never changes the asset.</summary>
public class AssetCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    /// <summary>The day it was checked; may be back-dated, never in the future.</summary>
    public DateOnly CheckDate { get; set; }
    public AssetCheckOutcome Outcome { get; set; } = AssetCheckOutcome.Ok;
    /// <summary>Required when the outcome is IssueFound.</summary>
    public string? Notes { get; set; }
    public Guid? CheckedById { get; set; }
    public ApplicationUser? CheckedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A place a department keeps its assets (§6.19). Owned by one department, never shared.</summary>
public class AssetLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Set when the location is created and never changed.</summary>
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Archived: no longer offered for new or changed assets; assets that have it keep it.</summary>
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
}

/// <summary>A kind of asset a department manages, with its own properties and check interval (§6.19). Owned by one department.</summary>
public class AssetType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Set when the type is created and never changed.</summary>
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Free text grouping types in the pickers and filters (IT equipment, Vehicles, Safety).</summary>
    public string? Category { get; set; }
    /// <summary>How often its assets should be checked; null = no scheduled checks.</summary>
    public int? CheckIntervalDays { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AssetTypeProperty> Properties { get; set; } = new List<AssetTypeProperty>();
    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
}

/// <summary>One extra field on every asset of a type (§6.19).</summary>
public class AssetTypeProperty
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public AssetPropertyType PropertyType { get; set; } = AssetPropertyType.Text;
    /// <summary>The options of a Choice property; empty for every other type.</summary>
    public List<string> Options { get; set; } = [];
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<AssetPropertyValue> Values { get; set; } = new List<AssetPropertyValue>();
}

/// <summary>An asset's value for one property, in the canonical form for its type (see AssetPropertyRules). A blank value is no row.</summary>
public class AssetPropertyValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public Guid AssetTypePropertyId { get; set; }
    public AssetTypeProperty Property { get; set; } = null!;
    public string Value { get; set; } = string.Empty;
}

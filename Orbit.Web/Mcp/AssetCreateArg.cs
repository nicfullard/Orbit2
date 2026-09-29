using System.ComponentModel;
using System.Text.Json;

namespace Orbit.Mcp;

/// <summary>
/// One asset to register: create_asset's arguments, and one entry of create_assets' items (spec §6.19, §7.1). Nothing is a C#
/// <c>required</c> member: an item that left one out would fail the whole call's binding with a message the client never sees,
/// so a missing name or type is refused for that item alone.
/// </summary>
public sealed class AssetCreateArg
{
    [Description("Name (required), e.g. \"Reception laptop\".")]
    public string? Name { get; set; }

    [Description("Asset type id (GUID) or name - one of the department's types (required).")]
    public string? AssetTypeId { get; set; }

    [Description("Its number in the ERP asset register, e.g. FA-004211 - only if it is on the ERP system; unique.")]
    public string? AssetNumber { get; set; }

    [Description("Optional idempotency key. Retrying with the same key returns the asset already registered instead of a duplicate.")]
    public string? IdempotencyKey { get; set; }

    [Description("Managing department id (GUID). Defaults to the key's own department; required for a key without one.")]
    public string? DepartmentId { get; set; }

    [Description("Free-text description.")]
    public string? Description { get; set; }

    [Description("Manufacturer, e.g. Dell.")]
    public string? Manufacturer { get; set; }

    [Description("Model, e.g. Latitude 5440.")]
    public string? Model { get; set; }

    [Description("Serial number.")]
    public string? SerialNumber { get; set; }

    [Description("Active (default), InStorage, Damaged, Lost or Disposed.")]
    public string? Status { get; set; }

    [Description("Location id (GUID) or name - one of the department's locations.")]
    public string? LocationId { get; set; }

    [Description("Purchase date yyyy-MM-dd.")]
    public string? PurchaseDate { get; set; }

    [Description("Purchase value, e.g. 18500.00, in the organisation's currency.")]
    public decimal? PurchaseValue { get; set; }

    [Description("Purchase order number.")]
    public string? PurchaseOrder { get; set; }

    [Description("Invoice number.")]
    public string? InvoiceNumber { get; set; }

    [Description("Supplier.")]
    public string? Supplier { get; set; }

    [Description("Warranty end date yyyy-MM-dd.")]
    public string? WarrantyExpiresOn { get; set; }

    [Description("With status Disposed: the disposal date yyyy-MM-dd (default today).")]
    public string? DisposedOn { get; set; }

    [Description("User ids (GUID) of the people who hold it - anyone active, in any department; list_users finds them.")]
    public string[]? AssigneeIds { get; set; }

    [Description("Property values by property name, e.g. {\"RAM (GB)\": 16}.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

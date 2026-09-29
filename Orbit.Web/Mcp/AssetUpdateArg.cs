using System.ComponentModel;
using System.Text.Json;

namespace Orbit.Mcp;

/// <summary>
/// One change to an asset: update_asset's arguments, and one entry of update_assets' items (spec §6.19, §7.1). Only what is given
/// changes; "none" clears an optional text or date field. assetId isn't a C# <c>required</c> member, so an item without one is
/// refused on its own rather than failing the whole call's binding.
/// </summary>
public sealed class AssetUpdateArg
{
    [Description("Asset id (GUID), or its ERP asset number when it has one (required).")]
    public string? AssetId { get; set; }

    [Description("ERP asset register number (unique), or \"none\" when the asset isn't on the ERP system.")]
    public string? AssetNumber { get; set; }

    [Description("New name.")]
    public string? Name { get; set; }

    [Description("Managing department id (GUID) to move the asset to; needs assetTypeId too.")]
    public string? DepartmentId { get; set; }

    [Description("Asset type id (GUID) or name - one of the (new) department's types.")]
    public string? AssetTypeId { get; set; }

    [Description("Description, or \"none\".")]
    public string? Description { get; set; }

    [Description("Manufacturer, or \"none\".")]
    public string? Manufacturer { get; set; }

    [Description("Model, or \"none\".")]
    public string? Model { get; set; }

    [Description("Serial number, or \"none\".")]
    public string? SerialNumber { get; set; }

    [Description("Active, InStorage, Damaged, Lost or Disposed.")]
    public string? Status { get; set; }

    [Description("Location id (GUID) or name - one of the (new) department's locations - or \"none\".")]
    public string? LocationId { get; set; }

    [Description("Purchase date yyyy-MM-dd, or \"none\".")]
    public string? PurchaseDate { get; set; }

    [Description("Purchase value, e.g. 18500.00, or \"none\".")]
    public string? PurchaseValue { get; set; }

    [Description("Purchase order number, or \"none\".")]
    public string? PurchaseOrder { get; set; }

    [Description("Invoice number, or \"none\".")]
    public string? InvoiceNumber { get; set; }

    [Description("Supplier, or \"none\".")]
    public string? Supplier { get; set; }

    [Description("Warranty end date yyyy-MM-dd, or \"none\".")]
    public string? WarrantyExpiresOn { get; set; }

    [Description("With status Disposed: the disposal date yyyy-MM-dd (default today).")]
    public string? DisposedOn { get; set; }

    [Description("The complete set of holders' user ids (GUID); [] unassigns everyone. Omit to leave them as they are.")]
    public string[]? AssigneeIds { get; set; }

    [Description("Property values to set by property name, e.g. {\"RAM (GB)\": 32}; \"none\" clears one.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

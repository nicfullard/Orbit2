using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Assets;

/// <summary>The asset form (spec §6.19). Property values are keyed by property id; the purchase value is parsed invariantly.</summary>
public sealed class AssetForm
{
    /// <summary>The ERP asset register number, when the asset is on it; blank for none.</summary>
    [StringLength(50)] public string? AssetNumber { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? AssetTypeId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public Guid? AssetLocationId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    /// <summary>Text, so "1234.50" means the same whatever the server's culture; parsed in <see cref="ToInput"/>.</summary>
    public string? PurchaseValue { get; set; }
    public string? PurchaseOrder { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Supplier { get; set; }
    public DateOnly? WarrantyExpiresOn { get; set; }
    public DateOnly? DisposedOn { get; set; }
    public List<Guid> AssigneeIds { get; set; } = [];
    /// <summary>Property values keyed by the property's id (as text). A field posted blank clears the value.</summary>
    public Dictionary<string, string?> Properties { get; set; } = new();

    /// <summary>
    /// The service input. Only the chosen type's properties are passed on - fields left over from another type (the page without
    /// script, say) are ignored rather than refused. Every one of them is a change, so the form is the whole state.
    /// </summary>
    public AssetInput ToInput(IEnumerable<AssetTypeProperty> typeProperties)
    {
        var ids = typeProperties.Select(p => p.Id).ToHashSet();
        var properties = new Dictionary<Guid, string?>();
        foreach (var (key, value) in Properties)
            if (Guid.TryParse(key, out var id) && ids.Contains(id)) properties[id] = value;
        return new AssetInput
        {
            AssetNumber = AssetNumber, Name = Name, Description = Description, DepartmentId = DepartmentId,
            AssetTypeId = AssetTypeId ?? Guid.Empty, Manufacturer = Manufacturer, Model = Model, SerialNumber = SerialNumber,
            Status = Status, AssetLocationId = AssetLocationId, PurchaseDate = PurchaseDate, PurchaseValue = ParseValue(PurchaseValue),
            PurchaseOrder = PurchaseOrder, InvoiceNumber = InvoiceNumber, Supplier = Supplier, WarrantyExpiresOn = WarrantyExpiresOn,
            DisposedOn = DisposedOn, AssigneeIds = AssigneeIds, Properties = properties
        };
    }

    public static AssetForm From(Asset a) => new()
    {
        AssetNumber = a.AssetNumber, Name = a.Name, Description = a.Description, DepartmentId = a.DepartmentId, AssetTypeId = a.AssetTypeId,
        Manufacturer = a.Manufacturer, Model = a.Model, SerialNumber = a.SerialNumber, Status = a.Status, AssetLocationId = a.AssetLocationId,
        PurchaseDate = a.PurchaseDate, PurchaseValue = a.PurchaseValue?.ToString("0.00", CultureInfo.InvariantCulture),
        PurchaseOrder = a.PurchaseOrder, InvoiceNumber = a.InvoiceNumber, Supplier = a.Supplier, WarrantyExpiresOn = a.WarrantyExpiresOn,
        DisposedOn = a.DisposedOn, AssigneeIds = a.Assignments.Select(x => x.UserId).ToList(),
        Properties = a.PropertyValues.ToDictionary(v => v.AssetTypePropertyId.ToString(), v => (string?)v.Value)
    };

    /// <summary>A money amount as typed: spaces ignored, a lone comma read as the decimal point.</summary>
    public static decimal? ParseValue(string? text)
    {
        var t = text?.Replace(" ", "").Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.Contains('.') && t.Count(c => c == ',') == 1) t = t.Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ValidationException($"The purchase value must be an amount like 1234.50; got \"{text}\".");
    }
}

/// <summary>
/// The people picker (spec §6.19): the chosen people as chips over a type-ahead search against /Assets/People, so it works with
/// any number of people. <see cref="SubmitOnPick"/> off: each chip is a hidden <see cref="FieldName"/> input posted with the form.
/// On: picking someone puts their id in <see cref="FieldName"/> and posts the enclosing form at once (the asset page's "Assign someone").
/// </summary>
public sealed class PersonPickerVm
{
    public required string Id { get; init; }
    public required string FieldName { get; init; }
    public required string SearchUrl { get; init; }
    public IReadOnlyList<UserSummary> Selected { get; init; } = [];
    public bool SubmitOnPick { get; init; }
    public string Placeholder { get; init; } = "Type a name, email or department...";
    public string Label { get; init; } = "Search for a person";
}

/// <summary>The type-driven property fields (spec §6.19), rendered in the form and re-rendered when the type changes.</summary>
public sealed class AssetPropertyFieldsVm
{
    public IReadOnlyList<AssetTypeProperty> Properties { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> Values { get; init; } = new Dictionary<Guid, string>();
    /// <summary>On a change of type: the current values that won't carry over ("RAM: 16").</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
    /// <summary>On a change of type: the names of the values that will carry over.</summary>
    public IReadOnlyList<string> Carried { get; init; } = [];
    public bool TypeChosen { get; init; }

    /// <summary>
    /// The fields for <paramref name="type"/>. With <paramref name="asset"/> on another type, the values that would carry over are
    /// filled in and the rest listed as dropped; <paramref name="posted"/> (a redisplayed form) wins over both.
    /// </summary>
    public static AssetPropertyFieldsVm For(AssetType? type, Asset? asset, IReadOnlyDictionary<string, string?>? posted = null)
    {
        if (type is null) return new AssetPropertyFieldsVm();
        var properties = type.Properties.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToList();
        var values = new Dictionary<Guid, string>();
        var dropped = new List<string>();
        var carried = new List<string>();
        if (asset is not null)
        {
            if (asset.AssetTypeId == type.Id)
            {
                foreach (var v in asset.PropertyValues) values[v.AssetTypePropertyId] = v.Value;
            }
            else
            {
                var oldProperties = asset.AssetType.Properties.ToDictionary(p => p.Id);
                var current = asset.PropertyValues.Where(v => oldProperties.ContainsKey(v.AssetTypePropertyId))
                    .Select(v => (Property: oldProperties[v.AssetTypePropertyId], v.Value)).ToList();
                var kept = AssetPropertyRules.CarryOver(current, properties);
                foreach (var (id, value) in kept) values[id] = value;
                carried = properties.Where(p => kept.ContainsKey(p.Id)).Select(p => p.Name).ToList();
                dropped = current.Where(c => !carried.Contains(c.Property.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(c => $"{c.Property.Name}: {AssetPropertyRules.Display(c.Property.PropertyType, c.Value)}").ToList();
            }
        }
        if (posted is not null)
        {
            foreach (var p in properties)
            {
                if (!posted.TryGetValue(p.Id.ToString(), out var raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) values.Remove(p.Id);
                else values[p.Id] = raw.Trim();
            }
        }
        return new AssetPropertyFieldsVm { Properties = properties, Values = values, Dropped = dropped, Carried = carried, TypeChosen = true };
    }
}

/// <summary>Everything the asset form offers, for the managing department it is on.</summary>
public sealed class AssetFormLookups
{
    public required Actor Actor { get; init; }
    public Guid? AssetId { get; init; }
    /// <summary>Registering anywhere (assets.create at All), or moving the asset (assets.edit at All).</summary>
    public bool CanChooseDepartment { get; init; }
    public string? DepartmentName { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    public IReadOnlyList<AssetType> Types { get; init; } = [];
    public IReadOnlyList<AssetLocation> Locations { get; init; } = [];
    /// <summary>The people currently chosen as holders, shown as chips in the picker (the rest are found by searching).</summary>
    public IReadOnlyList<UserSummary> Holders { get; init; } = [];
    public AssetSuggestions Suggestions { get; init; } = new([], [], []);
    public AssetPropertyFieldsVm PropertyFields { get; init; } = new();

    /// <summary>A department's types and locations, for the form's script when the managing department is changed.</summary>
    public static async Task<object> ChoicesAsync(Guid departmentId, AssetTypeService types, AssetLocationService locations, CancellationToken ct) => new
    {
        types = (await types.ListForDepartmentAsync(departmentId, ct: ct)).Select(t => new { id = t.Id, name = t.Name, category = t.Category }).ToList(),
        locations = (await locations.ListForDepartmentAsync(departmentId, ct: ct)).Select(l => new { id = l.Id, name = l.Name }).ToList()
    };

    public static async Task<AssetFormLookups> BuildAsync(
        Actor actor, AssetForm form, Asset? asset, bool canChooseDepartment, bool postedBack,
        DepartmentService departments, AssetTypeService types, AssetLocationService locations, UserDirectoryService users, AssetService assets,
        CancellationToken ct)
    {
        var allDepartments = await departments.ListAsync(includeArchived: true, ct);
        var deptItems = new List<SelectListItem>();
        if (canChooseDepartment)
        {
            deptItems.Add(new SelectListItem("- choose -", string.Empty));
            deptItems.AddRange(allDepartments.Where(d => !d.IsArchived || d.Id == form.DepartmentId)
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }
        var sameDepartment = asset is not null && asset.DepartmentId == form.DepartmentId;
        var typeList = form.DepartmentId is Guid dept
            ? await types.ListForDepartmentAsync(dept, sameDepartment ? asset!.AssetTypeId : null, ct)
            : [];
        var locationList = form.DepartmentId is Guid d2
            ? await locations.ListForDepartmentAsync(d2, sameDepartment ? asset!.AssetLocationId : null, ct)
            : [];
        var chosenType = typeList.FirstOrDefault(t => t.Id == form.AssetTypeId);
        return new AssetFormLookups
        {
            Actor = actor,
            AssetId = asset?.Id,
            CanChooseDepartment = canChooseDepartment,
            DepartmentName = allDepartments.FirstOrDefault(d => d.Id == form.DepartmentId)?.Name,
            Departments = deptItems,
            Types = typeList,
            Locations = locationList,
            Holders = await users.FindManyAsync(form.AssigneeIds, ct),
            Suggestions = await assets.SuggestionsAsync(ct),
            PropertyFields = AssetPropertyFieldsVm.For(chosenType, asset, postedBack ? form.Properties : null)
        };
    }
}

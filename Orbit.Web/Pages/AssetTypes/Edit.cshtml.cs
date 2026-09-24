using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.AssetTypes;

public class EditModel(AssetTypeService types) : OrbitPageModel
{
    public TypeForm Form { get; private set; } = new();
    public AssetType Type { get; private set; } = null!;
    public IReadOnlyList<AssetTypeProperty> Properties { get; private set; } = [];
    /// <summary>How many assets hold a value for each property - the delete confirmation says so (§6.19).</summary>
    public IReadOnlyDictionary<Guid, int> ValueCounts { get; private set; } = new Dictionary<Guid, int>();
    public IReadOnlyList<string> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = TypeForm.From(Type);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, TypeForm form, CancellationToken ct)
    {
        try
        {
            var type = await types.UpdateAsync(id, form.ToInput(), ct);
            Success($"Asset type \"{type.Name}\" saved.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        await LoadAsync(id, ct);
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        await types.SetArchivedAsync(id, archived, ct);
        Success(archived ? "Asset type archived: no longer offered for new assets. Assets that have it keep it." : "Asset type restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var type = await types.GetForConfigureAsync(id, ct);
            await types.DeleteAsync(id, ct);
            Success($"Asset type \"{type.Name}\" deleted.");
            return RedirectToPage("/AssetTypes/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    public async Task<IActionResult> OnPostAddPropertyAsync(Guid id, PropertyForm property, CancellationToken ct)
    {
        try
        {
            var added = await types.AddPropertyAsync(id, property.ToInput(), ct);
            Success($"Property \"{added.Name}\" added.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUpdatePropertyAsync(Guid id, Guid propertyId, PropertyForm property, CancellationToken ct)
    {
        try
        {
            var saved = await types.UpdatePropertyAsync(id, propertyId, property.ToInput(), ct);
            Success($"Property \"{saved.Name}\" saved.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeletePropertyAsync(Guid id, Guid propertyId, CancellationToken ct)
    {
        try
        {
            var values = await types.DeletePropertyAsync(id, propertyId, ct);
            Success(values == 0 ? "Property deleted." : $"Property deleted, with the values {values} {(values == 1 ? "asset" : "assets")} held for it.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMovePropertyAsync(Guid id, Guid propertyId, int direction, CancellationToken ct)
    {
        await types.MovePropertyAsync(id, propertyId, direction, ct);
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Type = await types.GetForConfigureAsync(id, ct);
        Properties = Type.Properties.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToList();
        var counts = new Dictionary<Guid, int>();
        foreach (var p in Properties) counts[p.Id] = (await types.ValueCountsAsync(p.Id, ct)).Values.Sum();
        ValueCounts = counts;
        Categories = await types.CategoriesAsync(Type.DepartmentId, ct);
    }
}

using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Assets;

public class EditModel(
    AssetService assets,
    AssetTypeService types,
    AssetLocationService locations,
    DepartmentService departments,
    UserDirectoryService users,
    IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public AssetForm Form { get; set; } = new();
    public Asset Asset { get; private set; } = null!;
    public AssetFormLookups Lookups { get; private set; } = null!;
    /// <summary>Only an asset with no checks can be deleted (§6.19); otherwise it is disposed.</summary>
    public bool CanDelete { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        await LoadAsync(actor, id, ct);
        Form = AssetForm.From(Asset);
        Lookups = await BuildAsync(actor, propertiesFromForm: false, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        await LoadAsync(actor, id, ct);
        if (!actor.CanAnywhere(Permission.AssetsEdit)) Form.DepartmentId = Asset.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var type = Form.AssetTypeId is Guid t ? await types.GetAsync(t, ct) : null;
                var saved = await assets.UpdateAsync(id, Form.ToInput(type?.Properties ?? []), ct);
                Success($"Asset \"{saved.Name}\" saved.");
                return RedirectToPage("/Assets/Details", new { id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await BuildAsync(actor, propertiesFromForm: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var asset = await assets.GetAsync(id, ct);
            await assets.DeleteAsync(id, ct);
            Success($"Asset \"{asset.Name}\" deleted.");
            return RedirectToPage("/Assets/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    /// <summary>The property fields for the type chosen on the form, with what would carry over from the asset's current type (§6.19).</summary>
    public async Task<IActionResult> OnGetPropertiesAsync(Guid id, Guid? typeId, CancellationToken ct)
    {
        var asset = await assets.GetAsync(id, ct);
        var type = typeId is Guid t ? await types.GetAsync(t, ct) : null;
        return Partial("_AssetPropertyFields", AssetPropertyFieldsVm.For(type, asset));
    }

    /// <summary>A department's types and locations, when the managing department is changed on the form (a move).</summary>
    public async Task<IActionResult> OnGetChoicesAsync(Guid departmentId, CancellationToken ct) =>
        new JsonResult(await AssetFormLookups.ChoicesAsync(departmentId, types, locations, ct));

    private async Task LoadAsync(Actor actor, Guid id, CancellationToken ct)
    {
        Asset = await assets.GetAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditAsset(actor, Asset), "You don't have permission to edit this asset.");
        CanDelete = AssetRules.DeleteBlocker(Asset.Checks.Count) is null;
    }

    private Task<AssetFormLookups> BuildAsync(Actor actor, bool propertiesFromForm, CancellationToken ct) =>
        AssetFormLookups.BuildAsync(actor, Form, Asset, actor.CanAnywhere(Permission.AssetsEdit), propertiesFromForm,
            departments, types, locations, users, assets, ct);
}

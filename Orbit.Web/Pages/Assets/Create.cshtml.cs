using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Assets;

public class CreateModel(
    AssetService assets,
    AssetTypeService types,
    AssetLocationService locations,
    DepartmentService departments,
    UserDirectoryService users,
    IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public AssetForm Form { get; set; } = new();
    public AssetFormLookups Lookups { get; private set; } = null!;
    /// <summary>The asset the form was copied from (the asset page's Copy, §6.19); null for a blank form.</summary>
    public Asset? CopiedFrom { get; private set; }

    public async Task OnGetAsync(Guid? departmentId, Guid? copyFrom, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (copyFrom is Guid sourceId)
        {
            // A copy stays in the source's department: its type and location belong to it.
            CopiedFrom = await assets.GetAsync(sourceId, ct);
            AccessPolicy.Require(AccessPolicy.CanCreateAssetIn(actor, CopiedFrom.DepartmentId),
                "You don't have permission to register assets in this asset's department.");
            Form = AssetForm.CopyOf(CopiedFrom);
        }
        else
        {
            Form.DepartmentId = actor.CanAnywhere(Permission.AssetsCreate) ? departmentId ?? actor.DepartmentId : actor.DepartmentId;
        }
        Lookups = await BuildAsync(actor, propertiesFromForm: CopiedFrom is not null, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.CanAnywhere(Permission.AssetsCreate)) Form.DepartmentId = actor.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var type = Form.AssetTypeId is Guid t ? await types.GetAsync(t, ct) : null;
                var asset = await assets.CreateAsync(Form.ToInput(type?.Properties ?? []), ct);
                Success($"Asset \"{asset.Name}\" registered.");
                return RedirectToPage("/Assets/Details", new { id = asset.Id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await BuildAsync(actor, propertiesFromForm: true, ct);
        return Page();
    }

    /// <summary>The property fields for the type chosen on the form (§6.19).</summary>
    public async Task<IActionResult> OnGetPropertiesAsync(Guid? typeId, CancellationToken ct)
    {
        var type = typeId is Guid t ? await types.GetAsync(t, ct) : null;
        return Partial("_AssetPropertyFields", AssetPropertyFieldsVm.For(type, null));
    }

    /// <summary>A department's types and locations, when the managing department is changed on the form.</summary>
    public async Task<IActionResult> OnGetChoicesAsync(Guid departmentId, CancellationToken ct) =>
        new JsonResult(await AssetFormLookups.ChoicesAsync(departmentId, types, locations, ct));

    private Task<AssetFormLookups> BuildAsync(Actor actor, bool propertiesFromForm, CancellationToken ct) =>
        AssetFormLookups.BuildAsync(actor, Form, null, actor.CanAnywhere(Permission.AssetsCreate), propertiesFromForm,
            departments, types, locations, users, assets, ct);
}

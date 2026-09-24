using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
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

    public async Task OnGetAsync(Guid? departmentId, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Form.DepartmentId = actor.CanAnywhere(Permission.AssetsCreate) ? departmentId ?? actor.DepartmentId : actor.DepartmentId;
        Lookups = await BuildAsync(actor, postedBack: false, ct);
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
        Lookups = await BuildAsync(actor, postedBack: true, ct);
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

    private Task<AssetFormLookups> BuildAsync(Actor actor, bool postedBack, CancellationToken ct) =>
        AssetFormLookups.BuildAsync(actor, Form, null, actor.CanAnywhere(Permission.AssetsCreate), postedBack,
            departments, types, locations, users, assets, ct);
}

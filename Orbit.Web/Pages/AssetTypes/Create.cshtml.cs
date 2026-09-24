using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.AssetTypes;

public class CreateModel(AssetTypeService types, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public TypeForm Form { get; set; } = new();
    public bool CanChooseDepartment { get; private set; }
    public string? DepartmentName { get; private set; }
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<string> Categories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Form.DepartmentId = actor.DepartmentId;
        await LoadAsync(actor, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.CanAnywhere(Permission.AssetsConfigure)) Form.DepartmentId = actor.DepartmentId;
        try
        {
            var type = await types.CreateAsync(Form.ToInput(), ct);
            Success($"Asset type \"{type.Name}\" created. Add its properties below.");
            return RedirectToPage("/AssetTypes/Edit", new { id = type.Id });
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        await LoadAsync(actor, ct);
        return Page();
    }

    private async Task LoadAsync(Actor actor, CancellationToken ct)
    {
        CanChooseDepartment = actor.CanAnywhere(Permission.AssetsConfigure);
        var all = await departments.ListAsync(includeArchived: false, ct);
        DepartmentName = all.FirstOrDefault(d => d.Id == Form.DepartmentId)?.Name;
        if (CanChooseDepartment)
            DepartmentItems = [new SelectListItem("- choose -", string.Empty), .. all.Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Form.DepartmentId))];
        Categories = Form.DepartmentId is Guid dept ? await types.CategoriesAsync(dept, ct) : [];
    }
}

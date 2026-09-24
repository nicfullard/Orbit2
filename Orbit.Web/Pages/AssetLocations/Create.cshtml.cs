using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.AssetLocations;

public class CreateModel(AssetLocationService locations, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public AssetLocationInput Form { get; set; } = new();
    public bool CanChooseDepartment { get; private set; }
    public string? DepartmentName { get; private set; }
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

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
            var location = await locations.CreateAsync(Form, ct);
            Success($"Location \"{location.Name}\" created.");
            return RedirectToPage("/AssetLocations/Index");
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
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.AssetTypes;

public class IndexModel(AssetTypeService types, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }
    [BindProperty(SupportsGet = true)] public bool ShowArchived { get; set; }

    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<AssetTypeListItem> Types { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Types = await types.ListAsync(ShowArchived, DepartmentId, ct);
        if (Actor.CanAnywhere(Permission.AssetsConfigure))
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Projects;

public class IndexModel(ProjectService projects, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public ProjectFilter Filter { get; set; } = new();
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<ProjectListItem> Projects { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Projects = await projects.ListAsync(Filter, ct);
        if (Actor.IsSystemAdmin)
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
    }
}

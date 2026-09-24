using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Helpers;

namespace Orbit.Pages.Projects;

public class IndexModel(ProjectService projects, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public ProjectFilter Filter { get; set; } = new();
    /// <summary>"Reset": forget the remembered filter and show the plain list.</summary>
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }
    /// <summary>The filter fields remembered for the session (§6.1, §6.2).</summary>
    public static readonly string[] RememberedFilters =
        [nameof(ProjectFilter.Search), nameof(ProjectFilter.DepartmentId), nameof(ProjectFilter.Status), nameof(ProjectFilter.IncludeArchived)];
    private const string MemoryKey = "projects";

    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<ProjectListItem> Projects { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        if (Reset)
        {
            FilterMemory.Forget(Response, MemoryKey);
            return RedirectToPage();
        }
        if (FilterMemory.IsExplicit(Request, RememberedFilters))
            FilterMemory.Remember(Request, Response, MemoryKey, Actor.UserId, RememberedFilters);
        else if (FilterMemory.Recall(Request, MemoryKey, Actor.UserId) is string remembered)
            return LocalRedirect(Request.Path + remembered);

        Projects = await projects.ListAsync(Filter, ct);
        if (Actor.CanAnywhere(Permission.ProjectsView))
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
        return Page();
    }
}

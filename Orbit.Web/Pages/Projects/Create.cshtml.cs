using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Projects;

public class CreateModel(ProjectService projects, DepartmentService departments, UserDirectoryService users, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public ProjectForm Form { get; set; } = new();
    public ProjectFormLookups Lookups { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Form.DepartmentId = actor.DepartmentId;
        Form.OwnerId = actor.UserId;
        Lookups = await ProjectFormLookups.BuildAsync(actor, departments, users, Form, actor.CanAnywhere(Permission.ProjectsCreate), ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.CanAnywhere(Permission.ProjectsCreate)) Form.DepartmentId = actor.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var project = await projects.CreateAsync(Form.ToInput(), ct);
                Success($"Project \"{project.Name}\" created.");
                return RedirectToPage("/Projects/Details", new { id = project.Id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await ProjectFormLookups.BuildAsync(actor, departments, users, Form, actor.CanAnywhere(Permission.ProjectsCreate), ct);
        return Page();
    }
}

using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Projects;

public class EditModel(ProjectService projects, DepartmentService departments, UserDirectoryService users, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public ProjectForm Form { get; set; } = new();
    public Project Project { get; private set; } = null!;
    public ProjectFormLookups Lookups { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Project = await projects.GetAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditProject(actor, Project), "Members can only edit projects they own.");
        Form = ProjectForm.From(Project);
        Lookups = await ProjectFormLookups.BuildAsync(actor, departments, users, Form, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Project = await projects.GetAsync(id, ct);
        if (!actor.IsSystemAdmin) Form.DepartmentId = Project.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var updated = await projects.UpdateAsync(id, Form.ToInput(), ct);
                Success($"Project \"{updated.Name}\" saved.");
                return RedirectToPage("/Projects/Details", new { id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await ProjectFormLookups.BuildAsync(actor, departments, users, Form, ct);
        return Page();
    }
}

using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public class CreateModel(
    TaskService tasks,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    SprintService sprints) : OrbitPageModel
{
    [BindProperty] public TaskForm Form { get; set; } = new();
    public TaskFormLookups Lookups { get; private set; } = null!;

    public async Task OnGetAsync(Guid? projectId, Guid? sprintId, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Form.ProjectId = projectId;
        Form.SprintId = sprintId;
        // A task defaults to its project's department; a System Admin can change that on the form (§6.2.1).
        Form.DepartmentId = projectId is Guid pid
            ? await projects.GetDepartmentIdAsync(pid, ct) ?? actor.DepartmentId
            : actor.DepartmentId;
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.IsSystemAdmin) Form.DepartmentId = actor.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var task = await tasks.CreateAsync(Form.ToInput(includeStatus: false), TaskSource.Manual, ct);
                Success($"Task \"{task.Title}\" created.");
                return RedirectToPage("/Tasks/Details", new { id = task.Id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form, ct);
        return Page();
    }
}

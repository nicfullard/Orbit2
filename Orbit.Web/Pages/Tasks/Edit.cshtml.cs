using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public class EditModel(
    TaskService tasks,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    SprintService sprints,
    TaskStructureService structure) : OrbitPageModel
{
    [BindProperty] public TaskForm Form { get; set; } = new();
    public TaskItem Task { get; private set; } = null!;
    public TaskFormLookups Lookups { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Task = await tasks.GetAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, Task), "You don't have permission to edit this task.");
        Form = TaskForm.From(Task);
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, structure, Form, Task.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Task = await tasks.GetAsync(id, ct);
        if (!actor.CanAnywhere(Permission.TasksCreate)) Form.DepartmentId = Task.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var updated = await tasks.UpdateAsync(id, Form.ToInput(includeStatus: true), ct);
                Success($"Task \"{updated.Title}\" saved.");
                return RedirectToPage("/Tasks/Details", new { id });
            }
            catch (ValidationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, structure, Form, Task.Id, ct);
        return Page();
    }
}

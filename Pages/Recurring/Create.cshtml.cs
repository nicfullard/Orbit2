using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Pages.Tasks;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Recurring;

public class CreateModel(
    RecurrenceService recurrence,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    SprintService sprints) : OrbitPageModel
{
    [BindProperty] public RecurringForm Form { get; set; } = new();
    public TaskFormLookups Lookups { get; private set; } = null!;

    public async Task OnGetAsync(Guid? projectId, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Form.ProjectId = projectId;
        // Defaults to the project's department; a System Admin can change that on the form (§6.2.1).
        Form.DepartmentId = projectId is Guid pid
            ? await projects.GetDepartmentIdAsync(pid, ct) ?? actor.DepartmentId
            : actor.DepartmentId;
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form.AsTaskForm(), ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        if (!actor.IsSystemAdmin) Form.DepartmentId = actor.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var def = await recurrence.CreateAsync(Form.ToInput(), ct);
                Success($"Recurring task \"{def.Title}\" created. Next due {Ui.Day(def.NextRunDate)}.");
                return RedirectToPage("/Recurring/Details", new { id = def.Id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form.AsTaskForm(), ct);
        return Page();
    }
}

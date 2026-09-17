using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Pages.Tasks;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Recurring;

public class EditModel(
    RecurrenceService recurrence,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    SprintService sprints) : OrbitPageModel
{
    [BindProperty] public RecurringForm Form { get; set; } = new();
    public RecurringTaskDefinition Definition { get; private set; } = null!;
    public TaskFormLookups Lookups { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Definition = await recurrence.GetAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditRecurring(actor, Definition), "Members can only edit recurring tasks they created or are assigned to.");
        Form = RecurringForm.From(Definition);
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form.AsTaskForm(), ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Definition = await recurrence.GetAsync(id, ct);
        if (!actor.IsSystemAdmin) Form.DepartmentId = Definition.DepartmentId;
        if (ModelState.IsValid)
        {
            try
            {
                var def = await recurrence.UpdateAsync(id, Form.ToInput(), ct);
                Success($"\"{def.Title}\" saved. Next due {Ui.Day(def.NextRunDate)}.");
                return RedirectToPage("/Recurring/Details", new { id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        Lookups = await TaskFormLookups.BuildAsync(actor, departments, projects, users, sprints, Form.AsTaskForm(), ct);
        return Page();
    }
}

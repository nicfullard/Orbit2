using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Recurring;

public class DetailsModel(RecurrenceService recurrence, TaskService tasks, IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public RecurringTaskDefinition Definition { get; private set; } = null!;
    public IReadOnlyList<DateOnly> Upcoming { get; private set; } = [];
    public IReadOnlyList<TaskItem> Generated { get; private set; } = [];
    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Definition = await recurrence.GetAsync(id, ct);
        CanEdit = AccessPolicy.CanEditRecurring(Actor, Definition);
        Upcoming = Definition.Active ? RecurrenceService.Upcoming(Definition, 10) : [];
        Generated = (await tasks.ListAsync(new TaskFilter { RecurringTaskDefinitionId = id, PageSize = 50 }, ct)).Items;
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool active, CancellationToken ct)
    {
        try
        {
            var d = await recurrence.SetActiveAsync(id, active, ct);
            Success(active ? $"Resumed. Next due {Ui.Day(d.NextRunDate)}." : "Paused.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostGenerateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var task = await recurrence.GenerateNowAsync(id, ct);
            Success($"Generated task due {Ui.Day(task.DueDate)}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }
}

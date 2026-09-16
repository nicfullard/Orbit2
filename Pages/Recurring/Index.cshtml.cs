using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Recurring;

public class IndexModel(RecurrenceService recurrence, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public RecurringFilter Filter { get; set; } = new();
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<RecurringTaskDefinition> Definitions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Definitions = await recurrence.ListAsync(Filter, ct);
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool active, CancellationToken ct)
    {
        try
        {
            var d = await recurrence.SetActiveAsync(id, active, ct);
            Success(active ? $"\"{d.Title}\" resumed. Next due {Ui.Day(d.NextRunDate)}." : $"\"{d.Title}\" paused.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }
}

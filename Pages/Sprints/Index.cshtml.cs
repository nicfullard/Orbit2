using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Sprints;

public class IndexModel(SprintService sprints, IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<SprintListItem> Sprints { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Sprints = await sprints.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var s = await sprints.StartAsync(id, ct);
            Success($"Sprint \"{s.Name}\" is now active.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var s = await sprints.CompleteAsync(id, ct);
            Success($"Sprint \"{s.Name}\" completed. Open tasks returned to the backlog.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }
}

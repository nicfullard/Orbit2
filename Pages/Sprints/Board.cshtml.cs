using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Sprints;

public class BoardModel(SprintService sprints, IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public SprintBoard Board { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Board = await sprints.GetBoardAsync(id, ct);
        return Page();
    }
}

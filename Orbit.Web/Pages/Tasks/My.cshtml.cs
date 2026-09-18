using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public class MyModel(TaskService tasks, TaskStructureService structure, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public bool IncludeClosed { get; set; }
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public IReadOnlyDictionary<Guid, Orbit.Application.Models.WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, Orbit.Application.Models.WaitingSummary>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Tasks = await tasks.GetMyTasksAsync(openOnly: !IncludeClosed, ct);
        Waiting = await structure.GetWaitingAsync(Tasks, ct);
    }
}

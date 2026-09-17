using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public class MyModel(TaskService tasks, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public bool IncludeClosed { get; set; }
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Tasks = await tasks.GetMyTasksAsync(openOnly: !IncludeClosed, ct);
    }
}

using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Time;

public class MyModel(TimeEntryService time, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    public Actor Actor { get; private set; } = null!;
    public MyTimeSummary Summary { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = To ?? today;
        var from = From ?? to.AddDays(-6);
        Summary = await time.MyTimeAsync(from, to, ct);
        From = Summary.From;
        To = Summary.To;
    }
}

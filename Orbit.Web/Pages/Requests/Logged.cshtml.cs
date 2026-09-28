using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Requests;

/// <summary>The confirmation after logging a request (spec §6.20): its number, who has it, and where to follow it.</summary>
public class LoggedModel(RequestService requests) : OrbitPageModel
{
    public MyRequest Logged { get; private set; } = null!;

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Logged = await requests.LoggedAsync(id, ct);
    }
}

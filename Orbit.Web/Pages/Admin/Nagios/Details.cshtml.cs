using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Nagios;

/// <summary>
/// One Nagios instance (spec §6.21): how it last answered, the problems Orbit is watching on it - each with the task it raised,
/// once it has - and the ones that ended lately. "Check now" queues a check in the Nagios job rather than running one here.
/// </summary>
public class DetailsModel(NagiosService nagios) : OrbitPageModel
{
    public NagiosInstanceDetails Details { get; private set; } = null!;

    public async Task OnGetAsync(Guid id, CancellationToken ct) => Details = await nagios.DetailsAsync(id, ct);

    public async Task<IActionResult> OnPostCheckAsync(Guid id, CancellationToken ct)
    {
        await nagios.CheckNowAsync(id, ct);
        Success("A check is on its way. Refresh in a few seconds to see what it found.");
        return RedirectToPage(new { id });
    }
}

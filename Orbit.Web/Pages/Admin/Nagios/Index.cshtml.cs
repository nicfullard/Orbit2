using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Nagios;

/// <summary>Nagios monitoring (spec §6.21, Admin &gt; Nagios): the instances Orbit watches and how each last answered.</summary>
public class IndexModel(NagiosService nagios) : OrbitPageModel
{
    public IReadOnlyList<NagiosInstanceListItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Items = await nagios.ListAsync(ct);
}

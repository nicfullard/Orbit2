using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Actions;

/// <summary>The action library (spec §6.20, Admin &gt; Actions): the scripts request flows run.</summary>
public class IndexModel(RequestActionService actions) : OrbitPageModel
{
    public IReadOnlyList<RequestAction> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Items = await actions.ListAsync(ct);
}

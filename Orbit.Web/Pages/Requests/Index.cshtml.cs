using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Requests;

/// <summary>The Requests page (spec §6.20): every department's request categories, and the requests the user has logged.</summary>
public class IndexModel(RequestService requests, IActorProvider actors) : OrbitPageModel
{
    public const int MyRequestsShown = 20;

    public IReadOnlyList<RequestCatalogueSection> Sections { get; private set; } = [];
    public IReadOnlyList<MyRequest> Mine { get; private set; } = [];
    /// <summary>requests.configure at any scope: an empty page points to Admin > Request flows.</summary>
    public bool CanConfigure { get; private set; }
    /// <summary>The viewer, so a request logged by or for someone else can say so.</summary>
    public Guid? MeId { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        MeId = actor.UserId;
        Sections = await requests.CatalogueAsync(ct);
        Mine = await requests.MyRequestsAsync(MyRequestsShown, ct);
        CanConfigure = actor.Has(Permission.RequestsConfigure);
    }
}

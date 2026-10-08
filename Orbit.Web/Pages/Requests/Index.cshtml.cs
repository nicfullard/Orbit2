using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Requests;

/// <summary>The Requests page (spec §6.20): every department's request categories, the steps waiting for the user, and the requests they follow.</summary>
public class IndexModel(RequestService requests, IActorProvider actors) : OrbitPageModel
{
    public const int MyRequestsShown = 20;

    public IReadOnlyList<RequestCatalogueSection> Sections { get; private set; } = [];
    public IReadOnlyList<RequestActionItem> Waiting { get; private set; } = [];
    public IReadOnlyList<Request> Mine { get; private set; } = [];
    /// <summary>requests.configure at any scope: an empty page points to Admin > Request flows.</summary>
    public bool CanConfigure { get; private set; }
    /// <summary>requests.manage at any scope: a link to the department's requests.</summary>
    public bool CanManage { get; private set; }
    public Guid? MeId { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        MeId = actor.UserId;
        Sections = await requests.CatalogueAsync(ct);
        Waiting = await requests.NeedsMyActionAsync(50, ct);
        Mine = await requests.MyRequestsAsync(MyRequestsShown, ct);
        CanConfigure = actor.Has(Permission.RequestsConfigure);
        CanManage = actor.Has(Permission.RequestsManage);
    }
}

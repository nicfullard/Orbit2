using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Helpers;

namespace Orbit.Pages;

public class IndexModel(DashboardService dashboard, AssetService assets, RequestService requests, IActorProvider actors) : OrbitPageModel
{
    public DashboardModel Dashboard { get; private set; } = null!;
    /// <summary>The Asset checks card (§6.19), for a role with assets.check; null otherwise.</summary>
    public AssetCheckSummary? AssetChecks { get; private set; }
    /// <summary>The My waiting approvals card (§6.9, §6.20), for a role that can open requests; null otherwise.</summary>
    public WaitingApprovalsVm? WaitingApprovals { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = await dashboard.BuildAsync(ct);
        AssetChecks = await assets.GetCheckSummaryAsync(ct);
        if (await requests.WaitingApprovalsAsync(ct: ct) is { } waiting)
            WaitingApprovals = new WaitingApprovalsVm(waiting, (await actors.GetAsync(ct)).UserId);
    }
}

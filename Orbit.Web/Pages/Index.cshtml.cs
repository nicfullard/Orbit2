using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages;

public class IndexModel(DashboardService dashboard, AssetService assets) : OrbitPageModel
{
    public DashboardModel Dashboard { get; private set; } = null!;
    /// <summary>The Asset checks card (§6.19), for a role with assets.check; null otherwise.</summary>
    public AssetCheckSummary? AssetChecks { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = await dashboard.BuildAsync(ct);
        AssetChecks = await assets.GetCheckSummaryAsync(ct);
    }
}

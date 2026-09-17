using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages;

public class IndexModel(DashboardService dashboard) : OrbitPageModel
{
    public DashboardModel Dashboard { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = await dashboard.BuildAsync(ct);
    }
}

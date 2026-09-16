using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Departments;

public class IndexModel(DepartmentService departments) : OrbitPageModel
{
    public IReadOnlyList<DepartmentOverview> Departments { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Departments = await departments.GetOverviewAsync(ct);
    }
}

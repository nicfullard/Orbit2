using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Departments;

public class DetailsModel(DepartmentService departments) : OrbitPageModel
{
    public DepartmentDetail Detail { get; private set; } = null!;

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Detail = await departments.GetDetailAsync(id, ct);
    }
}

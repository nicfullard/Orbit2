using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Roles;

public class IndexModel(RoleService roles) : OrbitPageModel
{
    public IReadOnlyList<RoleListItem> Roles { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Roles = await roles.ListAsync(ct);
    }
}

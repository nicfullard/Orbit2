using Orbit.Application.Models;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Users;

public class IndexModel(UserAdminService users) : OrbitPageModel
{
    public IReadOnlyList<UserSummary> Users { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Users = await users.ListAsync(ct);
    }
}

using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Assets;

/// <summary>
/// The people picker's search (spec §6.19): GET /Assets/People?q=... returns up to 20 active people matching a name, email or
/// department, as JSON. Only callers who may register or edit assets get results.
/// </summary>
public class PeopleModel(UserDirectoryService users) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        var people = await users.SearchAssetHolderCandidatesAsync(q, 20, ct);
        return new JsonResult(people.Select(u => new { id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName }));
    }
}

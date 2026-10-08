using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>
/// The flow builder's people search (spec §6.20): GET /RequestCatalogue/People?q=... returns up to 20 active people matching a name,
/// email or department, as JSON, for the pickers that name who performs a step, who approves and who is assigned. Behind the
/// folder's requests.configure door.
/// </summary>
public class PeopleModel(UserDirectoryService users) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        var people = await users.SearchAssetHolderCandidatesAsync(q, 20, ct);
        return new JsonResult(people.Select(u => new { id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName }));
    }
}

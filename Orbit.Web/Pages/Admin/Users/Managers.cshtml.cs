using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Users;

/// <summary>
/// The user forms' manager search (spec §6.5): GET /Admin/Users/Managers?q=...&amp;userId=... returns up to 20 active people matching a
/// name, email or department, as JSON, for the Manager picker - never the user being edited, who can't be their own manager.
/// Behind the folder's users.manage door.
/// </summary>
public class ManagersModel(UserDirectoryService users) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, Guid? userId, CancellationToken ct)
    {
        var people = await users.SearchManagerCandidatesAsync(q, userId, 20, ct);
        return new JsonResult(people.Select(u => new { id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName }));
    }
}

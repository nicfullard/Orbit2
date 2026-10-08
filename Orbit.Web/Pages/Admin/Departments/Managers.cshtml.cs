using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Admin.Departments;

/// <summary>
/// The department forms' manager search (spec §6.6): GET /Admin/Departments/Managers?q=... returns up to 20 active people matching
/// a name, email or department, as JSON, for the Manager picker. Behind the folder's departments.manage door.
/// </summary>
public class ManagersModel(UserDirectoryService users) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        var people = await users.SearchManagerCandidatesAsync(q, null, 20, ct);
        return new JsonResult(people.Select(u => new { id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName }));
    }
}

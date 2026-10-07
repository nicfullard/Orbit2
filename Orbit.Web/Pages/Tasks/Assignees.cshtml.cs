using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Tasks;

/// <summary>
/// The Assignees picker's search (spec §6.2.3): GET /Tasks/Assignees?departmentId=...&amp;q=... returns up to 20 people a task in
/// that department may be assigned to, as JSON - matching a name or email, or the first by name when nothing is typed.
/// <c>scope</c> is the person's own department, or null for someone who can be assigned anywhere; the form uses it to drop a
/// chip when the task's department changes. Only callers who may create or edit tasks get results.
/// </summary>
public class AssigneesModel(UserDirectoryService users) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(Guid? departmentId, string? q, CancellationToken ct)
    {
        var people = await users.SearchAssignableAsync(departmentId, q, 20, ct);
        return new JsonResult(people.Select(u => new
        {
            id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName,
            scope = u.CanViewAllTasks ? null : u.DepartmentId
        }));
    }
}

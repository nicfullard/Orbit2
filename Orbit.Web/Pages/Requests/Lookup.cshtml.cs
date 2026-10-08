using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;

namespace Orbit.Pages.Requests;

/// <summary>
/// The form pickers' search (spec §6.20): GET /Requests/Lookup?fieldId=...&amp;q=... returns up to 20 matches for an Asset, Project or
/// Person field within the field's scope, as JSON. A later form step adds its stepId, so a field scoped to the requester's department
/// knows whose. Behind the Requests folder's requests.submit door.
/// </summary>
public class LookupModel(RequestService requests) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(Guid fieldId, string? q, Guid? stepId, CancellationToken ct)
    {
        var matches = await requests.LookupAsync(fieldId, q, stepId, 20, ct);
        return new JsonResult(matches.Select(m => new { id = m.Id, name = m.Name, tag = m.Tag, detail = m.Detail }));
    }
}

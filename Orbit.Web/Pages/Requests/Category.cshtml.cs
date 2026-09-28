using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Requests;

/// <summary>One request category's options (spec §6.20): flows to walk through, and links that open elsewhere.</summary>
public class CategoryModel(RequestService requests) : OrbitPageModel
{
    public RequestCategory Category { get; private set; } = null!;

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Category = await requests.CategoryAsync(id, ct);
    }
}

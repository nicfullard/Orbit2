using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.ApiKeys;

public class IndexModel(ApiKeyService apiKeys) : OrbitPageModel
{
    public IReadOnlyList<ApiKey> Keys { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Keys = await apiKeys.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await apiKeys.RevokeAsync(id, ct);
            Success("API key revoked.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }
}

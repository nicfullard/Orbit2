using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Agents;

public class IndexModel(AgentService agents, IOptions<AppOptions> app) : OrbitPageModel
{
    public IReadOnlyList<AgentListItem> Agents { get; private set; } = [];
    public AgentSetupModel? Setup { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Agents = await agents.ListAsync(ct);
    }

    /// <summary>Re-renders rather than redirects: the new token exists only in this response and is never stored.</summary>
    public async Task<IActionResult> OnPostRegenerateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Setup = AgentSetupModel.For(await agents.RegenerateTokenAsync(id, ct), app.Value.BaseUrl);
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage();
        }
        Agents = await agents.ListAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await agents.RevokeAsync(id, ct);
            Success("Agent revoked and disconnected.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await agents.DeleteAsync(id, ct);
            Success("Agent deleted.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage();
    }
}

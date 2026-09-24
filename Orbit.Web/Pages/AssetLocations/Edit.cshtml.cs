using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.AssetLocations;

public class EditModel(AssetLocationService locations) : OrbitPageModel
{
    [BindProperty] public AssetLocationInput Form { get; set; } = new();
    public AssetLocation Location { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Location = await locations.GetForConfigureAsync(id, ct);
        Form = new AssetLocationInput { DepartmentId = Location.DepartmentId, Name = Location.Name, Description = Location.Description };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Location = await locations.GetForConfigureAsync(id, ct);
        try
        {
            var saved = await locations.UpdateAsync(id, Form, ct);
            Success($"Location \"{saved.Name}\" saved.");
            return RedirectToPage("/AssetLocations/Index");
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        await locations.SetArchivedAsync(id, archived, ct);
        Success(archived ? "Location archived: no longer offered for assets. Assets there keep it." : "Location restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var location = await locations.GetForConfigureAsync(id, ct);
            await locations.DeleteAsync(id, ct);
            Success($"Location \"{location.Name}\" deleted.");
            return RedirectToPage("/AssetLocations/Index");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }
}

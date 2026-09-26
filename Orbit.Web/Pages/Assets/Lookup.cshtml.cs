using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Assets;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Assets;

/// <summary>
/// The asset picker's search (spec §6.19): GET /Assets/Lookup?q=... returns up to 20 assets the caller can see, not disposed, whose
/// name, ERP asset number, serial number, manufacturer, model, location or holder matches, as JSON - an exact ERP number or serial
/// first, flagged <c>exact</c> so a scan followed by Enter picks it. Behind the Assets folder's assets.view door.
/// </summary>
public class LookupModel(AssetService assets) : OrbitPageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        var matches = await assets.SearchForPickerAsync(q, 20, ct);
        return new JsonResult(matches.Select(a => new
        {
            id = a.Id,
            name = AssetRules.Label(a.AssetNumber, a.Name),
            tag = a.TypeName,
            detail = Detail(a.TypeName, a.LocationName, a.Holders, a.Status),
            exact = a.Exact
        }));
    }

    /// <summary>"Laptop · Head office · Jane Smith, Ann Lee +1 · Damaged": what tells two similar assets apart.</summary>
    private static string Detail(string type, string? location, IReadOnlyList<string> holders, AssetStatus status)
    {
        var parts = new List<string> { type };
        if (location is not null) parts.Add(location);
        if (holders.Count > 0) parts.Add(string.Join(", ", holders.Take(2)) + (holders.Count > 2 ? $" +{holders.Count - 2}" : ""));
        if (status != AssetStatus.Active) parts.Add(status.Label());
        return string.Join(" · ", parts);
    }
}

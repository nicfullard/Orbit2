using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Helpers;

namespace Orbit.Pages.Assets;

public class IndexModel(AssetService assets, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? AssetTypeId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? LocationId { get; set; }
    /// <summary>Blank = not disposed, "all", or a status.</summary>
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    /// <summary>Blank = anyone, "me", "none", "deactivated", or a person's id.</summary>
    [BindProperty(SupportsGet = true)] public string? Holder { get; set; }
    [BindProperty(SupportsGet = true)] public AssetCheckFilter? Check { get; set; }
    [BindProperty(SupportsGet = true)] public AssetWarrantyFilter? Warranty { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    /// <summary>"Reset": forget the remembered filter and show the plain list.</summary>
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }

    /// <summary>The filter fields remembered for the browser session (§6.19, like the Tasks list's).</summary>
    public static readonly string[] RememberedFilters =
        [nameof(Search), nameof(DepartmentId), nameof(AssetTypeId), nameof(LocationId), nameof(Status), nameof(Holder), nameof(Check), nameof(Warranty)];
    private const string MemoryKey = "assets";

    public Actor Actor { get; private set; } = null!;
    public PagedResult<AssetListItem> Result { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<AssetFilterOption> TypeOptions { get; private set; } = [];
    public IReadOnlyList<AssetFilterOption> LocationOptions { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name, bool IsActive)> Holders { get; private set; } = [];
    public bool ShowDepartment { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        if (Reset)
        {
            FilterMemory.Forget(Response, MemoryKey);
            return RedirectToPage();
        }
        if (FilterMemory.IsExplicit(Request, RememberedFilters))
            FilterMemory.Remember(Request, Response, MemoryKey, Actor.UserId, RememberedFilters);
        else if (FilterMemory.Recall(Request, MemoryKey, Actor.UserId) is string remembered)
            return LocalRedirect(Request.Path + remembered);

        var filter = new AssetFilter
        {
            Search = Search, DepartmentId = DepartmentId, AssetTypeId = AssetTypeId, LocationId = LocationId, Status = Status,
            Check = Check, Warranty = Warranty, Page = PageNumber, PageSize = 50
        };
        switch (Holder?.Trim().ToLowerInvariant())
        {
            case null or "": break;
            case "me": filter.AssignedToMe = true; break;
            case "none": filter.Unassigned = true; break;
            case "deactivated": filter.HeldByDeactivated = true; break;
            default: filter.AssignedToUserId = Guid.TryParse(Holder, out var person) ? person : null; break;
        }
        Result = await assets.ListAsync(filter, ct);
        (TypeOptions, LocationOptions) = await assets.FilterOptionsAsync(ct);
        Holders = await assets.HoldersAsync(ct);
        var everywhere = Actor.CanAnywhere(Permission.AssetsView);
        if (everywhere)
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
        ShowDepartment = everywhere || Result.Items.Any(i => i.Asset.DepartmentId != Actor.DepartmentId);
        return Page();
    }

    /// <summary>
    /// Quick check (§6.19): the dialog posts each scan (or, for a serial on several assets, the one chosen) here and gets JSON back,
    /// so scanning carries on without a page load. Refusals come back as an Error with the message, not as a flash and redirect.
    /// </summary>
    public async Task<IActionResult> OnPostQuickCheckAsync(string? scanned, Guid? assetId, CancellationToken ct)
    {
        try
        {
            var result = assetId is Guid id ? await assets.QuickCheckAssetAsync(id, ct) : await assets.QuickCheckAsync(scanned, ct);
            return new JsonResult(new
            {
                status = result.Status.ToString(),
                assets = result.Assets.Select(a => new
                {
                    id = a.Id, label = AssetRules.Label(a.AssetNumber, a.Name), url = Url.Page("/Assets/Details", new { id = a.Id })
                })
            });
        }
        catch (OrbitException ex)
        {
            return new JsonResult(new { status = "Error", message = ex.Message });
        }
    }

    /// <summary>A filter option's label: its department too when that isn't the viewer's own (a held asset, or company-wide scope).</summary>
    public string OptionLabel(AssetFilterOption o) =>
        o.DepartmentId == Actor.DepartmentId && !Actor.CanAnywhere(Permission.AssetsView)
            ? (o.Group is { Length: > 0 } g ? $"{g} / {o.Name}" : o.Name)
            : (o.Group is { Length: > 0 } g2 ? $"{g2} / {o.Name} ({o.DepartmentName})" : $"{o.Name} ({o.DepartmentName})");

    public bool WarrantyExpired(Orbit.Data.Entities.Asset a) => a.Status != Orbit.Data.Entities.AssetStatus.Disposed && assets.WarrantyExpired(a);
    public bool WarrantyExpiring(Orbit.Data.Entities.Asset a) => a.Status != Orbit.Data.Entities.AssetStatus.Disposed && assets.WarrantyExpiring(a);

    public string PageUrl(int page) => Url.Page("/Assets/Index", new
    {
        Search, DepartmentId, AssetTypeId, LocationId, Status, Holder, Check, Warranty, page
    })!;
}

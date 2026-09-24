using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The asset register (spec §6.19): listing and filtering, registering, editing (type changes with carry-over, moves between
/// departments, disposal, who holds it), deleting an asset registered in error, and checks. Every rule is enforced here, for the
/// pages and the MCP tools alike; the pure parts live in <see cref="AssetRules"/>, <see cref="AssetPropertyRules"/> and
/// <see cref="AssetCheckSchedule"/>.
/// </summary>
public sealed class AssetService(ApplicationDbContext db, IActorProvider actors, AuditService audit, IOptions<AssetOptions> options)
{
    private const string CantSee = "You don't have permission to see this asset.";

    private AssetOptions Windows => options.Value;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The assets the caller may see (assets.view), filtered and paged - ordered by name, since many assets have no ERP asset
    /// number, then by that number.
    /// </summary>
    public async Task<PagedResult<AssetListItem>> ListAsync(AssetFilter filter, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = Filtered(Scoping.Assets(db.Assets.AsNoTracking(), actor), filter, actor);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 500);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(a => a.Name).ThenBy(a => a.AssetNumber).ThenBy(a => a.Id)
            .Include(a => a.Department)
            .Include(a => a.AssetType)
            .Include(a => a.AssetLocation)
            .Include(a => a.Assignments).ThenInclude(x => x.User)
            .AsSplitQuery()
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AssetListItem>(items.Select(Item).ToList(), page, pageSize, total);
    }

    /// <summary>One asset in full: type and properties, values, holders, checks (newest first). Needs assets.view reaching it.</summary>
    public async Task<Asset> GetAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await db.Assets.AsNoTracking()
            .Include(a => a.Department)
            .Include(a => a.AssetType).ThenInclude(t => t.Properties)
            .Include(a => a.AssetLocation)
            .Include(a => a.CreatedBy)
            .Include(a => a.Assignments).ThenInclude(x => x.User).ThenInclude(u => u.Department)
            .Include(a => a.Assignments).ThenInclude(x => x.AssignedBy)
            .Include(a => a.PropertyValues)
            .Include(a => a.Checks).ThenInclude(c => c.CheckedBy)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), CantSee);
        asset.Checks = asset.Checks.OrderByDescending(c => c.CheckDate).ThenByDescending(c => c.CreatedAt).ToList();
        return asset;
    }

    /// <summary>An asset argument as the MCP tools take it: its GUID, or its ERP asset number (exact, ignoring case) when it has one.</summary>
    public async Task<Guid> ResolveIdAsync(string? value, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ValidationException("assetId is required.");
        if (Guid.TryParse(text, out var id)) return id;
        var lower = text.ToLowerInvariant();
        return await db.Assets.AsNoTracking().Where(a => a.AssetNumber != null && a.AssetNumber.ToLower() == lower).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"No asset has the number {text}.");
    }

    /// <summary>Where an asset stands against its check schedule, with this deployment's due-soon window.</summary>
    public AssetListItem Item(Asset asset)
    {
        var due = AssetCheckSchedule.NextDue(asset);
        return new AssetListItem(asset, due, AssetCheckSchedule.StateOf(due, Today, Windows.CheckDueSoonDays));
    }

    public bool WarrantyExpired(Asset asset) => asset.WarrantyExpiresOn is DateOnly d && d < Today;

    public bool WarrantyExpiring(Asset asset) =>
        asset.WarrantyExpiresOn is DateOnly d && d >= Today && d <= Today.AddDays(Windows.WarrantyExpiringDays);

    /// <summary>
    /// Other assets the caller can see that look like the same item: the same manufacturer and serial number, ignoring case
    /// (spec §6.19). Flagged on the asset page; nothing is refused.
    /// </summary>
    public async Task<IReadOnlyList<AssetRef>> PossibleDuplicatesAsync(Asset asset, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(asset.SerialNumber)) return [];
        var serial = asset.SerialNumber.Trim().ToLower();
        var maker = asset.Manufacturer?.Trim().ToLower();
        return await Scoping.Assets(db.Assets.AsNoTracking(), actor)
            .Where(a => a.Id != asset.Id && a.SerialNumber != null && a.SerialNumber.ToLower() == serial
                && (maker == null ? a.Manufacturer == null : a.Manufacturer != null && a.Manufacturer.ToLower() == maker))
            .OrderBy(a => a.Name)
            .Select(a => new AssetRef(a.Id, a.AssetNumber, a.Name))
            .ToListAsync(ct);
    }

    /// <summary>The types and locations found among the assets the caller can see, for the list filters.</summary>
    public async Task<(IReadOnlyList<AssetFilterOption> Types, IReadOnlyList<AssetFilterOption> Locations)> FilterOptionsAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var visible = Scoping.Assets(db.Assets.AsNoTracking(), actor);
        var types = await visible
            .Select(a => new { a.AssetType.Id, a.AssetType.Name, a.AssetType.Category, a.AssetType.DepartmentId, Dept = a.AssetType.Department.Name })
            .Distinct().ToListAsync(ct);
        var locations = await visible.Where(a => a.AssetLocationId != null)
            .Select(a => new { a.AssetLocation!.Id, a.AssetLocation.Name, a.AssetLocation.DepartmentId, Dept = a.AssetLocation.Department.Name })
            .Distinct().ToListAsync(ct);
        return (
            types.OrderBy(t => t.Dept).ThenBy(t => t.Category).ThenBy(t => t.Name)
                .Select(t => new AssetFilterOption(t.Id, t.Name, t.Category, t.DepartmentId, t.Dept)).ToList(),
            locations.OrderBy(l => l.Dept).ThenBy(l => l.Name)
                .Select(l => new AssetFilterOption(l.Id, l.Name, null, l.DepartmentId, l.Dept)).ToList());
    }

    /// <summary>The people holding the assets the caller can see, for the "assigned to" filter.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name, bool IsActive)>> HoldersAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var rows = await Scoping.Assets(db.Assets.AsNoTracking(), actor)
            .SelectMany(a => a.Assignments.Select(x => new { x.UserId, x.User.DisplayName, x.User.IsActive }))
            .Distinct().ToListAsync(ct);
        return rows.OrderBy(r => r.DisplayName).Select(r => (r.UserId, r.DisplayName, r.IsActive)).ToList();
    }

    /// <summary>Values already in use for the free-text fields, suggested on the asset form.</summary>
    public async Task<AssetSuggestions> SuggestionsAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var visible = Scoping.Assets(db.Assets.AsNoTracking(), actor);
        async Task<IReadOnlyList<string>> Distinct(IQueryable<string?> values) =>
            await values.Where(v => v != null && v != "").Select(v => v!).Distinct().OrderBy(v => v).Take(300).ToListAsync(ct);
        return new AssetSuggestions(
            await Distinct(visible.Select(a => a.Manufacturer)),
            await Distinct(visible.Select(a => a.Model)),
            await Distinct(visible.Select(a => a.Supplier)));
    }

    /// <summary>
    /// The dashboard's Asset checks card: overdue, due soon and last-check-not-OK counts within the caller's assets.check reach, and
    /// how many assets they hold. Null when the role has no assets.check.
    /// </summary>
    public async Task<AssetCheckSummary?> GetCheckSummaryAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var scope = actor.ScopeOf(Permission.AssetsCheck);
        if (scope == PermissionScope.None) return null;
        var reach = Scoping.Assets(db.Assets.AsNoTracking(), actor, Permission.AssetsCheck);
        var overdue = await Filtered(reach, new AssetFilter { Check = AssetCheckFilter.Overdue }, actor).CountAsync(ct);
        var dueSoon = await Filtered(reach, new AssetFilter { Check = AssetCheckFilter.DueSoon }, actor).CountAsync(ct);
        var notOk = await Filtered(reach, new AssetFilter { Check = AssetCheckFilter.NotOk }, actor).CountAsync(ct);
        var me = actor.UserId;
        var held = me is null ? 0 : await db.Assets.CountAsync(a => a.Status != AssetStatus.Disposed && a.Assignments.Any(x => x.UserId == me), ct);
        return new AssetCheckSummary(scope, overdue, dueSoon, notOk, held);
    }

    /// <summary>How many assets (not disposed) a person holds - shown on their Admin &gt; Users page for the leaver process.</summary>
    public async Task<int> CountHeldByAsync(Guid userId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.Assets.CountAsync(a => a.Status != AssetStatus.Disposed && a.Assignments.Any(x => x.UserId == userId), ct);
    }

    // ---------------------------------------------------------------- writing

    public async Task<Asset> CreateAsync(AssetInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var today = Today;
        // A retried create_asset with the same key gets the asset it already registered (many assets have no ERP number
        // whose uniqueness would otherwise catch the retry).
        var idempotencyKey = AssetRules.Clean(input.IdempotencyKey, 200, "The idempotency key");
        if (idempotencyKey is not null
            && await db.Assets.AsNoTracking().Where(a => a.IdempotencyKey == idempotencyKey).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct) is Guid already)
            return await GetAsync(already, ct);

        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        AccessPolicy.Require(AccessPolicy.CanCreateAssetIn(actor, departmentId), "You don't have permission to register assets in this department.");
        var dept = await ActiveDepartmentAsync(departmentId, ct);

        var number = AssetRules.NormaliseAssetNumber(input.AssetNumber);
        if (number is not null) await RequireUniqueNumberAsync(actor, number, null, ct);
        var type = await TypeAsync(input.AssetTypeId, ct);
        var location = await LocationAsync(input.AssetLocationId, ct);
        AssetRules.CheckTypeAndLocation(departmentId, dept.Name, type, location, moving: false);
        var values = AssetPropertyRules.Resolve(type.Properties.ToList(), new Dictionary<Guid, string>(), input.Properties);
        var purchaseDate = AssetRules.CleanPurchaseDate(input.PurchaseDate, today);
        var disposedOn = AssetRules.DisposalDate(input.Status, input.DisposedOn, purchaseDate, today);
        // A disposed asset is held by nobody (§6.19).
        IReadOnlyList<ApplicationUser> holders = input.Status == AssetStatus.Disposed ? [] : await HoldersToAddAsync(input.AssigneeIds ?? [], [], ct);

        var now = DateTime.UtcNow;
        var asset = new Asset
        {
            DepartmentId = departmentId,
            AssetNumber = number,
            Name = AssetRules.RequireName(input.Name),
            Description = AssetRules.Clean(input.Description, 4000, "The description"),
            AssetTypeId = type.Id,
            Manufacturer = AssetRules.Clean(input.Manufacturer, 200, "The manufacturer"),
            Model = AssetRules.Clean(input.Model, 200, "The model"),
            SerialNumber = AssetRules.Clean(input.SerialNumber, 100, "The serial number"),
            Status = input.Status,
            AssetLocationId = location?.Id,
            PurchaseDate = purchaseDate,
            PurchaseValue = AssetRules.CleanValue(input.PurchaseValue),
            PurchaseOrder = AssetRules.Clean(input.PurchaseOrder, 100, "The purchase order"),
            InvoiceNumber = AssetRules.Clean(input.InvoiceNumber, 100, "The invoice number"),
            Supplier = AssetRules.Clean(input.Supplier, 200, "The supplier"),
            WarrantyExpiresOn = input.WarrantyExpiresOn,
            DisposedOn = disposedOn,
            CreatedAt = now,
            CreatedById = actor.UserId,
            UpdatedAt = now,
            IdempotencyKey = idempotencyKey
        };
        foreach (var (propertyId, value) in values)
            asset.PropertyValues.Add(new AssetPropertyValue { AssetId = asset.Id, AssetTypePropertyId = propertyId, Value = value });
        foreach (var holder in holders)
            asset.Assignments.Add(new AssetAssignment { AssetId = asset.Id, UserId = holder.Id, AssignedAt = now, AssignedById = actor.UserId });
        db.Assets.Add(asset);

        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.Created, departmentId, Summary(asset), new
        {
            asset.AssetNumber, asset.Name, type = type.Name, asset.Status, location = location?.Name,
            assignees = holders.Select(h => h.DisplayName).ToList(),
            properties = type.Properties.Where(p => values.ContainsKey(p.Id)).ToDictionary(p => p.Name, p => values[p.Id])
        });
        await SaveAsync(number, ct);
        return await GetAsync(asset.Id, ct);
    }

    /// <summary>
    /// Save an asset as described. A move to another department needs assets.edit there too and one of its types (and one of its
    /// locations, or none). A change of type carries over values with a namesake on the new type. Disposing sets the disposal date and
    /// removes every holder; reinstating clears the date.
    /// </summary>
    public async Task<Asset> UpdateAsync(Guid id, AssetInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var today = Today;
        var asset = await db.Assets
            .Include(a => a.Department)
            .Include(a => a.AssetType).ThenInclude(t => t.Properties)
            .Include(a => a.AssetLocation)
            .Include(a => a.Assignments).ThenInclude(x => x.User)
            .Include(a => a.PropertyValues)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), CantSee);
        AccessPolicy.Require(AccessPolicy.CanEditAsset(actor, asset), "You don't have permission to edit this asset.");

        var departmentId = input.DepartmentId ?? asset.DepartmentId;
        var moving = departmentId != asset.DepartmentId;
        var dept = asset.Department;
        if (moving)
        {
            AccessPolicy.Require(AccessPolicy.CanMoveAssetTo(actor, departmentId), "Moving an asset to another department needs the Edit assets permission in that department too.");
            dept = await ActiveDepartmentAsync(departmentId, ct);
        }

        var number = AssetRules.NormaliseAssetNumber(input.AssetNumber);
        if (number is not null && !AssetRules.SameAssetNumber(number, asset.AssetNumber)) await RequireUniqueNumberAsync(actor, number, asset.Id, ct);
        var type = input.AssetTypeId == asset.AssetTypeId ? asset.AssetType : await TypeAsync(input.AssetTypeId, ct);
        var location = input.AssetLocationId == asset.AssetLocationId ? asset.AssetLocation : await LocationAsync(input.AssetLocationId, ct);
        AssetRules.CheckTypeAndLocation(departmentId, dept.Name, type, location, moving);

        // Property values: the current ones, or those that carry over to a new type, with the input's changes applied.
        var oldProperties = asset.AssetType.Properties.ToDictionary(p => p.Id);
        var current = asset.PropertyValues.Where(v => oldProperties.ContainsKey(v.AssetTypePropertyId))
            .Select(v => (Property: oldProperties[v.AssetTypePropertyId], v.Value)).ToList();
        IReadOnlyDictionary<Guid, string> existing = type.Id == asset.AssetTypeId
            ? current.ToDictionary(c => c.Property.Id, c => c.Value)
            : AssetPropertyRules.CarryOver(current, type.Properties);
        var values = AssetPropertyRules.Resolve(type.Properties.ToList(), existing, input.Properties);

        var purchaseDate = AssetRules.CleanPurchaseDate(input.PurchaseDate, today);
        var disposedOn = AssetRules.DisposalDate(input.Status, input.DisposedOn, purchaseDate, today);
        var currentHolders = asset.Assignments.Select(x => x.UserId).ToList();
        List<Guid> wanted = input.Status == AssetStatus.Disposed ? [] : (input.AssigneeIds ?? currentHolders).Distinct().ToList();
        var added = await HoldersToAddAsync(wanted, currentHolders, ct);
        var removed = asset.Assignments.Where(x => !wanted.Contains(x.UserId)).ToList();

        var name = AssetRules.RequireName(input.Name);
        var description = AssetRules.Clean(input.Description, 4000, "The description");
        var manufacturer = AssetRules.Clean(input.Manufacturer, 200, "The manufacturer");
        var model = AssetRules.Clean(input.Model, 200, "The model");
        var serial = AssetRules.Clean(input.SerialNumber, 100, "The serial number");
        var value = AssetRules.CleanValue(input.PurchaseValue);
        var order = AssetRules.Clean(input.PurchaseOrder, 100, "The purchase order");
        var invoice = AssetRules.Clean(input.InvoiceNumber, 100, "The invoice number");
        var supplier = AssetRules.Clean(input.Supplier, 200, "The supplier");

        var changes = new ChangeSet()
            .Track("assetNumber", asset.AssetNumber, number)
            .TrackText("name", asset.Name, name)
            .TrackText("description", asset.Description, description)
            .Track("department", asset.Department.Name, dept.Name)
            .Track("assetType", asset.AssetType.Name, type.Name)
            .TrackText("manufacturer", asset.Manufacturer, manufacturer)
            .TrackText("model", asset.Model, model)
            .TrackText("serialNumber", asset.SerialNumber, serial)
            .Track("location", asset.AssetLocation?.Name, location?.Name)
            .Track("purchaseDate", asset.PurchaseDate, purchaseDate)
            .Track("purchaseValue", asset.PurchaseValue, value)
            .TrackText("purchaseOrder", asset.PurchaseOrder, order)
            .TrackText("invoiceNumber", asset.InvoiceNumber, invoice)
            .TrackText("supplier", asset.Supplier, supplier)
            .Track("warrantyExpiresOn", asset.WarrantyExpiresOn, input.WarrantyExpiresOn);
        // Property values by name, so a type change reads as values removed, carried over and set (§6.19).
        var before = current.ToDictionary(c => c.Property.Name, c => c.Value, StringComparer.OrdinalIgnoreCase);
        var after = type.Properties.Where(p => values.ContainsKey(p.Id)).ToDictionary(p => p.Name, p => values[p.Id], StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
            changes.Track($"property:{propertyName}", before.GetValueOrDefault(propertyName), after.GetValueOrDefault(propertyName));
        var statusChanged = asset.Status != input.Status || asset.DisposedOn != disposedOn;
        if (!changes.HasChanges && !statusChanged && added.Count == 0 && removed.Count == 0) return await GetAsync(id, ct);

        var now = DateTime.UtcNow;
        var previousStatus = asset.Status;
        var previousDisposedOn = asset.DisposedOn;
        asset.AssetNumber = number;
        asset.Name = name;
        asset.Description = description;
        asset.DepartmentId = departmentId;
        asset.AssetTypeId = type.Id;
        asset.Manufacturer = manufacturer;
        asset.Model = model;
        asset.SerialNumber = serial;
        asset.Status = input.Status;
        asset.AssetLocationId = location?.Id;
        asset.PurchaseDate = purchaseDate;
        asset.PurchaseValue = value;
        asset.PurchaseOrder = order;
        asset.InvoiceNumber = invoice;
        asset.Supplier = supplier;
        asset.WarrantyExpiresOn = input.WarrantyExpiresOn;
        asset.DisposedOn = disposedOn;
        asset.UpdatedAt = now;

        foreach (var row in asset.PropertyValues.ToList())
        {
            if (!values.TryGetValue(row.AssetTypePropertyId, out var kept)) db.AssetPropertyValues.Remove(row);
            else if (row.Value != kept) row.Value = kept;
        }
        foreach (var (propertyId, v) in values)
            if (asset.PropertyValues.All(x => x.AssetTypePropertyId != propertyId))
                db.AssetPropertyValues.Add(new AssetPropertyValue { AssetId = asset.Id, AssetTypePropertyId = propertyId, Value = v });

        var summary = Summary(asset);
        foreach (var row in removed)
        {
            db.AssetAssignments.Remove(row);
            audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.AssetUnassigned, departmentId, summary,
                new { userId = row.UserId, user = row.User.DisplayName, disposed = input.Status == AssetStatus.Disposed ? true : (bool?)null });
        }
        foreach (var holder in added)
        {
            db.AssetAssignments.Add(new AssetAssignment { AssetId = asset.Id, UserId = holder.Id, AssignedAt = now, AssignedById = actor.UserId });
            audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.AssetAssigned, departmentId, summary, new { userId = holder.Id, user = holder.DisplayName });
        }
        if (statusChanged)
            audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.StatusChanged, departmentId, summary,
                new { status = new { from = previousStatus, to = input.Status }, disposedOn = new { from = previousDisposedOn, to = disposedOn } });
        if (changes.HasChanges)
            audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.Updated, departmentId, summary, changes.Changes);
        await SaveAsync(number, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Delete an asset registered in error - only while it has no checks (§6.19); otherwise dispose of it.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await db.Assets.Include(a => a.Assignments).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), CantSee);
        AccessPolicy.Require(AccessPolicy.CanEditAsset(actor, asset), "You don't have permission to delete this asset.");
        if (AssetRules.DeleteBlocker(await db.AssetChecks.CountAsync(c => c.AssetId == id, ct)) is string blocker)
            throw new ValidationException(blocker);
        // Holders, values, comments and files go with it (cascade); the audit entry records that it existed.
        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.Deleted, asset.DepartmentId, Summary(asset), new { asset.AssetNumber, asset.Name });
        db.Assets.Remove(asset);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- holders

    /// <summary>
    /// Give an asset to one more person, straight from the asset page (§6.19). Only the holders change - no other field is saved -
    /// so nothing unrelated (a property made required since the asset was registered, say) can block a hand-over. Returns the
    /// person's name.
    /// </summary>
    public async Task<string> AssignAsync(Guid assetId, Guid userId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await HoldersEditableAsync(actor, assetId, ct);
        if (asset.Status == AssetStatus.Disposed) throw new ValidationException("A disposed asset can't be assigned.");
        var existing = asset.Assignments.FirstOrDefault(x => x.UserId == userId);
        if (existing is not null) return existing.User.DisplayName;
        var holder = (await HoldersToAddAsync([userId], [], ct)).Single();
        var now = DateTime.UtcNow;
        db.AssetAssignments.Add(new AssetAssignment { AssetId = asset.Id, UserId = holder.Id, AssignedAt = now, AssignedById = actor.UserId });
        asset.UpdatedAt = now;
        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.AssetAssigned, asset.DepartmentId, Summary(asset), new { userId = holder.Id, user = holder.DisplayName });
        await db.SaveChangesAsync(ct);
        return holder.DisplayName;
    }

    /// <summary>Take an asset off one person, straight from the asset page (§6.19). Returns their name, or null when they didn't hold it.</summary>
    public async Task<string?> UnassignAsync(Guid assetId, Guid userId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await HoldersEditableAsync(actor, assetId, ct);
        var row = asset.Assignments.FirstOrDefault(x => x.UserId == userId);
        if (row is null) return null;
        db.AssetAssignments.Remove(row);
        asset.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.AssetUnassigned, asset.DepartmentId, Summary(asset), new { userId, user = row.User.DisplayName });
        await db.SaveChangesAsync(ct);
        return row.User.DisplayName;
    }

    /// <summary>An asset whose holders the caller may change: assets.edit reaching it.</summary>
    private async Task<Asset> HoldersEditableAsync(Actor actor, Guid assetId, CancellationToken ct)
    {
        var asset = await db.Assets.Include(a => a.Assignments).ThenInclude(x => x.User).FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), CantSee);
        AccessPolicy.Require(AccessPolicy.CanEditAsset(actor, asset), "You don't have permission to change who holds this asset.");
        return asset;
    }

    // ---------------------------------------------------------------- checks

    /// <summary>Record a check (§6.19). It never changes the asset; it sets the asset's last check if it is the latest.</summary>
    public async Task<AssetCheck> RecordCheckAsync(Guid assetId, AssetCheckInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await db.Assets.Include(a => a.Assignments).Include(a => a.Checks).FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new NotFoundException("Asset not found.");
        var assigned = AccessPolicy.IsAssigned(actor, asset);
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, assigned), CantSee);
        var today = Today;
        var date = input.CheckDate ?? today;
        var notes = AssetRules.ValidateCheck(asset.Status, input.Outcome, date, input.Notes, today);
        AccessPolicy.Require(AccessPolicy.CanCheckAsset(actor, asset, assigned), "You don't have permission to record a check on this asset.");

        var check = new AssetCheck
        {
            AssetId = asset.Id, CheckDate = date, Outcome = input.Outcome, Notes = notes, CheckedById = actor.UserId, CreatedAt = DateTime.UtcNow
        };
        db.AssetChecks.Add(check);
        asset.Checks.Add(check);
        SetLastCheck(asset);
        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.CheckRecorded, asset.DepartmentId, Summary(asset),
            new { checkId = check.Id, checkDate = date, outcome = input.Outcome, notes });
        await db.SaveChangesAsync(ct);
        return check;
    }

    /// <summary>"Confirm I have it": the holder's own OK check, dated today.</summary>
    public Task<AssetCheck> ConfirmHeldAsync(Guid assetId, CancellationToken ct = default) =>
        RecordCheckAsync(assetId, new AssetCheckInput { Outcome = AssetCheckOutcome.Ok, Notes = "Confirmed by the holder." }, ct);

    /// <summary>Remove a check recorded in error: whoever recorded it, or anyone who may edit the asset.</summary>
    public async Task RemoveCheckAsync(Guid assetId, Guid checkId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await db.Assets.Include(a => a.Assignments).Include(a => a.Checks).FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), CantSee);
        var check = asset.Checks.FirstOrDefault(c => c.Id == checkId) ?? throw new NotFoundException("Check not found.");
        AccessPolicy.Require(AccessPolicy.CanRemoveCheck(actor, check, asset), "Only whoever recorded a check, or someone who may edit the asset, can remove it.");
        db.AssetChecks.Remove(check);
        asset.Checks.Remove(check);
        SetLastCheck(asset);
        audit.Add(actor, AuditEntity.Asset, asset.Id, AuditAction.CheckRemoved, asset.DepartmentId, Summary(asset),
            new { checkId = check.Id, checkDate = check.CheckDate, outcome = check.Outcome, notes = check.Notes });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    public static string Summary(Asset a) => AssetRules.Label(a.AssetNumber, a.Name);

    private static void SetLastCheck(Asset asset)
    {
        var latest = AssetCheckSchedule.Latest(asset.Checks);
        asset.LastCheckedOn = latest?.CheckDate;
        asset.LastCheckOutcome = latest?.Outcome;
    }

    /// <summary>The list filters (§6.19), on top of whatever scope the caller has applied.</summary>
    private IQueryable<Asset> Filtered(IQueryable<Asset> q, AssetFilter f, Actor actor)
    {
        var today = Today;
        if (f.DepartmentId is Guid dept) q = q.Where(a => a.DepartmentId == dept);
        if (f.AssetTypeId is Guid type) q = q.Where(a => a.AssetTypeId == type);
        if (!string.IsNullOrWhiteSpace(f.Category))
        {
            var category = f.Category.Trim();
            q = q.Where(a => a.AssetType.Category == category);
        }
        if (f.LocationId is Guid location) q = q.Where(a => a.AssetLocationId == location);

        var status = f.Status?.Trim();
        if (string.Equals(status, AssetFilter.AllStatuses, StringComparison.OrdinalIgnoreCase)) { }
        else if (!string.IsNullOrEmpty(status))
        {
            var s = Enum.TryParse<AssetStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException($"status must be one of: {string.Join(", ", Enum.GetNames<AssetStatus>())}, or all.");
            q = q.Where(a => a.Status == s);
        }
        else q = q.Where(a => a.Status != AssetStatus.Disposed);

        if (f.Unassigned) q = q.Where(a => !a.Assignments.Any());
        else if (f.AssignedToMe)
        {
            var me = actor.UserId;
            q = me is null ? q.Where(a => false) : q.Where(a => a.Assignments.Any(x => x.UserId == me));
        }
        else if (f.AssignedToUserId is Guid user) q = q.Where(a => a.Assignments.Any(x => x.UserId == user));
        if (f.HeldByDeactivated) q = q.Where(a => a.Assignments.Any(x => !x.User.IsActive));

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = $"%{f.Search.Trim()}%";
            q = q.Where(a => (a.AssetNumber != null && EF.Functions.ILike(a.AssetNumber, pattern)) || EF.Functions.ILike(a.Name, pattern)
                || (a.SerialNumber != null && EF.Functions.ILike(a.SerialNumber, pattern))
                || (a.Manufacturer != null && EF.Functions.ILike(a.Manufacturer, pattern))
                || (a.Model != null && EF.Functions.ILike(a.Model, pattern)));
        }

        if (f.Check is AssetCheckFilter check)
        {
            var soon = today.AddDays(Math.Max(0, Windows.CheckDueSoonDays));
            switch (check)
            {
                case AssetCheckFilter.NotOk:
                    q = q.Where(a => a.Status != AssetStatus.Disposed && a.LastCheckOutcome != null && a.LastCheckOutcome != AssetCheckOutcome.Ok);
                    break;
                case AssetCheckFilter.Never:
                    q = q.Where(a => a.Status != AssetStatus.Disposed && a.LastCheckedOn == null);
                    break;
                default:
                    // The next check due (§6.19): one interval after the last check, or after registration when never checked.
                    q = q.Where(a => a.AssetType.CheckIntervalDays != null
                        && (a.Status == AssetStatus.Active || a.Status == AssetStatus.InStorage || a.Status == AssetStatus.Damaged));
                    q = check switch
                    {
                        AssetCheckFilter.Overdue => q.Where(a =>
                            (a.LastCheckedOn ?? DateOnly.FromDateTime(a.CreatedAt)).AddDays(a.AssetType.CheckIntervalDays!.Value) < today),
                        AssetCheckFilter.DueSoon => q.Where(a =>
                            (a.LastCheckedOn ?? DateOnly.FromDateTime(a.CreatedAt)).AddDays(a.AssetType.CheckIntervalDays!.Value) >= today
                            && (a.LastCheckedOn ?? DateOnly.FromDateTime(a.CreatedAt)).AddDays(a.AssetType.CheckIntervalDays!.Value) <= soon),
                        _ => q.Where(a =>
                            (a.LastCheckedOn ?? DateOnly.FromDateTime(a.CreatedAt)).AddDays(a.AssetType.CheckIntervalDays!.Value) <= soon)
                    };
                    break;
            }
        }

        if (f.Warranty == AssetWarrantyFilter.Expiring)
        {
            var until = today.AddDays(Math.Max(0, Windows.WarrantyExpiringDays));
            q = q.Where(a => a.WarrantyExpiresOn != null && a.WarrantyExpiresOn >= today && a.WarrantyExpiresOn <= until);
        }
        else if (f.Warranty == AssetWarrantyFilter.Expired)
            q = q.Where(a => a.WarrantyExpiresOn != null && a.WarrantyExpiresOn < today);
        return q;
    }

    private async Task<Department> ActiveDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        return dept;
    }

    /// <summary>A type chosen for an asset: it must exist and not be archived (an asset keeps an archived type it already has).</summary>
    private async Task<AssetType> TypeAsync(Guid typeId, CancellationToken ct)
    {
        if (typeId == Guid.Empty) throw new ValidationException("An asset type is required.");
        var type = await db.AssetTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new NotFoundException("Asset type not found.");
        if (type.IsArchived) throw new ValidationException($"The asset type \"{type.Name}\" is archived.");
        return type;
    }

    /// <summary>A location chosen for an asset, or none: it must exist and not be archived.</summary>
    private async Task<AssetLocation?> LocationAsync(Guid? locationId, CancellationToken ct)
    {
        if (locationId is not Guid id) return null;
        var location = await db.AssetLocations.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("Location not found.");
        if (location.IsArchived) throw new ValidationException($"The location \"{location.Name}\" is archived.");
        return location;
    }

    /// <summary>The people to add as holders: anyone active in Orbit, whatever their department - but never the synthetic Claude user.</summary>
    private async Task<IReadOnlyList<ApplicationUser>> HoldersToAddAsync(IEnumerable<Guid> wanted, IReadOnlyCollection<Guid> current, CancellationToken ct)
    {
        var ids = wanted.Distinct().Where(u => !current.Contains(u)).ToList();
        if (ids.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        foreach (var id in ids)
        {
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw new NotFoundException("A person to assign the asset to wasn't found.");
            if (user.IsSystemAccount) throw new ValidationException("Assets can only be assigned to people.");
            if (!user.IsActive) throw new ValidationException($"{user.DisplayName} is deactivated and can't be given an asset.");
        }
        return users;
    }

    /// <summary>An ERP asset number, when given, is unique ignoring case. The clash names the other asset only when the caller can see it.</summary>
    private async Task RequireUniqueNumberAsync(Actor actor, string number, Guid? exceptId, CancellationToken ct)
    {
        var lower = number.ToLowerInvariant();
        var other = await db.Assets.AsNoTracking().Include(a => a.Assignments)
            .FirstOrDefaultAsync(a => a.AssetNumber != null && a.AssetNumber.ToLower() == lower && a.Id != exceptId, ct);
        if (other is null) return;
        throw new ValidationException(AccessPolicy.CanViewAsset(actor, other, AccessPolicy.IsAssigned(actor, other))
            ? $"Asset number {number} is already used by \"{other.Name}\"."
            : $"Asset number {number} is already taken.");
    }

    /// <summary>Save, turning the unique index's backstop (two people registering the same number at once) into the usual message.</summary>
    private async Task SaveAsync(string? number, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && pg.ConstraintName == "IX_Assets_AssetNumber_Lower")
        {
            throw new ValidationException($"Asset number {number} is already taken.");
        }
    }
}

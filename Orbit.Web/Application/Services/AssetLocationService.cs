using Microsoft.EntityFrameworkCore;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The places each department keeps its assets (spec §6.19). A location belongs to one department, set when it is created and
/// never changed; assets.configure reaching that department manages it. Reading locations needs nothing more.
/// </summary>
public sealed class AssetLocationService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    /// <summary>The Locations pages: the locations within the caller's assets.configure reach, with how many assets are there.</summary>
    public async Task<IReadOnlyList<AssetLocationListItem>> ListAsync(bool includeArchived = false, Guid? departmentId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = Scoping.AssetLocations(db.AssetLocations.AsNoTracking().Include(l => l.Department), actor);
        if (!includeArchived) q = q.Where(l => !l.IsArchived);
        if (departmentId is Guid d) q = q.Where(l => l.DepartmentId == d);
        var rows = await q.OrderBy(l => l.Department.Name).ThenBy(l => l.Name)
            .Select(l => new { Location = l, Assets = l.Assets.Count(a => a.Status != AssetStatus.Disposed) })
            .ToListAsync(ct);
        return rows.Select(r => new AssetLocationListItem(r.Location, r.Assets)).ToList();
    }

    /// <summary>A department's active locations for the asset form. <paramref name="includeLocationId"/> keeps an asset's current one even when archived.</summary>
    public async Task<IReadOnlyList<AssetLocation>> ListForDepartmentAsync(Guid departmentId, Guid? includeLocationId = null, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.AssetLocations.AsNoTracking()
            .Where(l => l.DepartmentId == departmentId && (!l.IsArchived || l.Id == includeLocationId))
            .OrderBy(l => l.Name).ToListAsync(ct);
    }

    /// <summary>Every active location with its department - the list_asset_locations lookup, open to every caller.</summary>
    public async Task<IReadOnlyList<AssetLocation>> ListActiveAsync(Guid? departmentId = null, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var q = db.AssetLocations.AsNoTracking().Include(l => l.Department).Where(l => !l.IsArchived);
        if (departmentId is Guid d) q = q.Where(l => l.DepartmentId == d);
        return await q.OrderBy(l => l.Department.Name).ThenBy(l => l.Name).ToListAsync(ct);
    }

    /// <summary>One location with its department, archived or not. Open to every caller (the get_asset_location lookup).</summary>
    public async Task<AssetLocation> GetAsync(Guid id, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.AssetLocations.AsNoTracking().Include(l => l.Department).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("Location not found.");
    }

    /// <summary>A location for its edit page: assets.configure must reach its department.</summary>
    public async Task<AssetLocation> GetForConfigureAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var location = await db.AssetLocations.AsNoTracking().Include(l => l.Department).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("Location not found.");
        RequireConfigure(actor, location.DepartmentId);
        return location;
    }

    /// <summary>A department's location by name (ignoring case) - how the MCP tools accept a location name in place of its id.</summary>
    public async Task<Guid?> FindIdByNameAsync(Guid? departmentId, string name, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var dept = departmentId ?? actor.DepartmentId
            ?? throw new ValidationException("Finding a location by name needs a department (pass departmentId).");
        var n = name.Trim().ToLowerInvariant();
        return await db.AssetLocations.AsNoTracking()
            .Where(l => l.DepartmentId == dept && l.Name.ToLower() == n)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<AssetLocation> CreateAsync(AssetLocationInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        RequireConfigure(actor, departmentId);
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        var name = AssetRules.RequireName(input.Name);
        await RequireUniqueNameAsync(departmentId, name, null, ct);

        var location = new AssetLocation
        {
            DepartmentId = departmentId,
            Name = name,
            Description = AssetRules.Clean(input.Description, 1000, "The description"),
            CreatedAt = DateTime.UtcNow
        };
        db.AssetLocations.Add(location);
        audit.Add(actor, AuditEntity.AssetLocation, location.Id, AuditAction.Created, departmentId, location.Name,
            new { location.Name, department = dept.Name });
        await db.SaveChangesAsync(ct);
        return location;
    }

    public async Task<AssetLocation> UpdateAsync(Guid id, AssetLocationInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var location = await db.AssetLocations.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Location not found.");
        RequireConfigure(actor, location.DepartmentId);
        var name = AssetRules.RequireName(input.Name);
        if (!string.Equals(name, location.Name, StringComparison.OrdinalIgnoreCase)) await RequireUniqueNameAsync(location.DepartmentId, name, id, ct);
        var description = AssetRules.Clean(input.Description, 1000, "The description");
        var changes = new ChangeSet()
            .TrackText("name", location.Name, name)
            .TrackText("description", location.Description, description);
        if (!changes.HasChanges) return location;
        location.Name = name;
        location.Description = description;
        audit.Add(actor, AuditEntity.AssetLocation, location.Id, AuditAction.Updated, location.DepartmentId, location.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return location;
    }

    /// <summary>Archive: no longer offered for new or changed assets; the assets there keep it.</summary>
    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var location = await db.AssetLocations.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Location not found.");
        RequireConfigure(actor, location.DepartmentId);
        if (location.IsArchived == archived) return;
        location.IsArchived = archived;
        audit.Add(actor, AuditEntity.AssetLocation, location.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, location.DepartmentId, location.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete a location no asset refers to - disposed ones included; otherwise archive it.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var location = await db.AssetLocations.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Location not found.");
        RequireConfigure(actor, location.DepartmentId);
        var used = await db.Assets.CountAsync(a => a.AssetLocationId == id, ct);
        if (used > 0)
            throw new ValidationException($"\"{location.Name}\" can't be deleted while {(used == 1 ? "1 asset refers" : $"{used} assets refer")} to it (disposed ones included). Archive it instead.");
        db.AssetLocations.Remove(location);
        audit.Add(actor, AuditEntity.AssetLocation, location.Id, AuditAction.Deleted, location.DepartmentId, location.Name, new { location.Name });
        await db.SaveChangesAsync(ct);
    }

    private static void RequireConfigure(Actor actor, Guid departmentId) =>
        AccessPolicy.Require(AccessPolicy.CanConfigureAssetsIn(actor, departmentId), "You don't have permission to configure this department's asset locations.");

    private async Task RequireUniqueNameAsync(Guid departmentId, string name, Guid? exceptId, CancellationToken ct)
    {
        var n = name.ToLowerInvariant();
        if (await db.AssetLocations.AnyAsync(l => l.DepartmentId == departmentId && l.Name.ToLower() == n && l.Id != exceptId, ct))
            throw new ValidationException($"The department already has a location called \"{name}\".");
    }
}

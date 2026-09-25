using Microsoft.EntityFrameworkCore;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Asset types and their properties (spec §6.19). Each type belongs to one department, set when it is created and never changed;
/// assets.configure reaching that department manages it. Reading types needs nothing more: they are configuration, not records.
/// </summary>
public sealed class AssetTypeService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    public const int MaxCheckIntervalDays = 3650;

    /// <summary>The Asset types pages: the types within the caller's assets.configure reach, with property and asset counts.</summary>
    public async Task<IReadOnlyList<AssetTypeListItem>> ListAsync(bool includeArchived = false, Guid? departmentId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = Scoping.AssetTypes(db.AssetTypes.AsNoTracking().Include(t => t.Department), actor);
        if (!includeArchived) q = q.Where(t => !t.IsArchived);
        if (departmentId is Guid d) q = q.Where(t => t.DepartmentId == d);
        var rows = await q.OrderBy(t => t.Department.Name).ThenBy(t => t.Category).ThenBy(t => t.Name)
            .Select(t => new { Type = t, Properties = t.Properties.Count, Assets = t.Assets.Count(a => a.Status != AssetStatus.Disposed) })
            .ToListAsync(ct);
        return rows.Select(r => new AssetTypeListItem(r.Type, r.Properties, r.Assets)).ToList();
    }

    /// <summary>
    /// A department's active types, with their properties, for the asset form's picker. <paramref name="includeTypeId"/> keeps an
    /// asset's current type in the list even when it has since been archived.
    /// </summary>
    public async Task<IReadOnlyList<AssetType>> ListForDepartmentAsync(Guid departmentId, Guid? includeTypeId = null, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.AssetTypes.AsNoTracking().Include(t => t.Properties)
            .Where(t => t.DepartmentId == departmentId && (!t.IsArchived || t.Id == includeTypeId))
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>Every active type with its department and properties - the list_asset_types lookup, open to every caller.</summary>
    public async Task<IReadOnlyList<AssetType>> ListActiveAsync(Guid? departmentId = null, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var q = db.AssetTypes.AsNoTracking().Include(t => t.Department).Include(t => t.Properties).Where(t => !t.IsArchived);
        if (departmentId is Guid d) q = q.Where(t => t.DepartmentId == d);
        return await q.OrderBy(t => t.Department.Name).ThenBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>One type with its department and properties in display order. Open to every caller (the asset form renders its properties).</summary>
    public async Task<AssetType> GetAsync(Guid id, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await Load(db.AssetTypes.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Asset type not found.");
    }

    /// <summary>A type for its edit page: assets.configure must reach its department.</summary>
    public async Task<AssetType> GetForConfigureAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await Load(db.AssetTypes.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        return type;
    }

    /// <summary>A department's type by name (ignoring case) - how the MCP tools accept a type name in place of its id.</summary>
    public async Task<Guid?> FindIdByNameAsync(Guid? departmentId, string name, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var dept = departmentId ?? actor.DepartmentId
            ?? throw new ValidationException("Finding an asset type by name needs a department (pass departmentId).");
        var n = name.Trim().ToLowerInvariant();
        return await db.AssetTypes.AsNoTracking()
            .Where(t => t.DepartmentId == dept && t.Name.ToLower() == n)
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The categories already in use in a department, suggested on the type form.</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(Guid departmentId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.AssetTypes.AsNoTracking().Where(t => t.DepartmentId == departmentId && t.Category != null)
            .Select(t => t.Category!).Distinct().OrderBy(c => c).ToListAsync(ct);
    }

    /// <summary>How many assets hold each value of a property - for the delete confirmation and the change rules.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ValueCountsAsync(Guid propertyId, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.AssetPropertyValues.AsNoTracking().Where(v => v.AssetTypePropertyId == propertyId)
            .GroupBy(v => v.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, StringComparer.Ordinal, ct);
    }

    public async Task<AssetType> CreateAsync(AssetTypeInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        RequireConfigure(actor, departmentId);
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        var name = RequireName(input.Name);
        await RequireUniqueNameAsync(departmentId, name, null, ct);

        var now = DateTime.UtcNow;
        var type = new AssetType
        {
            DepartmentId = departmentId,
            Name = name,
            Description = AssetRules.Clean(input.Description, 1000, "The description"),
            Category = AssetRules.Clean(input.Category, 100, "The category"),
            CheckIntervalDays = CleanInterval(input.CheckIntervalDays),
            CreatedAt = now,
            UpdatedAt = now
        };
        // First properties (create_asset_type): validated here and saved with the type, so a bad one creates nothing.
        foreach (var p in input.Properties)
        {
            var (propertyName, options) = AssetPropertyRules.ValidateDefinition(p.Name, p.PropertyType, p.Options);
            RequireUniquePropertyName(type, propertyName, null);
            type.Properties.Add(new AssetTypeProperty
            {
                AssetTypeId = type.Id,
                Name = propertyName,
                PropertyType = p.PropertyType,
                Options = options,
                IsRequired = p.IsRequired,
                DisplayOrder = type.Properties.Count + 1
            });
        }
        db.AssetTypes.Add(type);
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Created, departmentId, type.Name,
            new
            {
                type.Name, type.Category, type.CheckIntervalDays, department = dept.Name,
                properties = type.Properties.Count == 0 ? null
                    : type.Properties.Select(p => new { name = p.Name, type = p.PropertyType, required = p.IsRequired, options = p.Options }).ToList()
            });
        await db.SaveChangesAsync(ct);
        return type;
    }

    public async Task<AssetType> UpdateAsync(Guid id, AssetTypeInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var name = RequireName(input.Name);
        if (!string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase)) await RequireUniqueNameAsync(type.DepartmentId, name, id, ct);
        var description = AssetRules.Clean(input.Description, 1000, "The description");
        var category = AssetRules.Clean(input.Category, 100, "The category");
        var interval = CleanInterval(input.CheckIntervalDays);
        var changes = new ChangeSet()
            .TrackText("name", type.Name, name)
            .TrackText("description", type.Description, description)
            .TrackText("category", type.Category, category)
            .Track("checkIntervalDays", type.CheckIntervalDays, interval);
        if (!changes.HasChanges) return type;
        type.Name = name;
        type.Description = description;
        type.Category = category;
        type.CheckIntervalDays = interval;
        type.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Updated, type.DepartmentId, type.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return type;
    }

    /// <summary>
    /// The update_asset_type tool: the type's own fields, archiving, and property changes and additions in one transaction, so a
    /// refused property change leaves nothing half-applied. Each step keeps its own checks and audit entry, as on the edit page.
    /// </summary>
    public async Task<AssetType> ApplyAsync(Guid id, AssetTypeInput input, bool? archived, IReadOnlyList<AssetPropertyChange> properties, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await UpdateAsync(id, input, ct);
        if (archived is bool a) await SetArchivedAsync(id, a, ct);
        foreach (var change in properties)
        {
            if (change.PropertyId is Guid propertyId) await UpdatePropertyAsync(id, propertyId, change.Input, ct);
            else await AddPropertyAsync(id, change.Input, ct);
        }
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Archive: no longer offered for new or changed assets; the assets that have it keep it.</summary>
    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        if (type.IsArchived == archived) return;
        type.IsArchived = archived;
        type.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.AssetType, type.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, type.DepartmentId, type.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete a type no asset uses - disposed ones included; otherwise archive it.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var used = await db.Assets.CountAsync(a => a.AssetTypeId == id, ct);
        if (used > 0)
            throw new ValidationException($"\"{type.Name}\" can't be deleted while {(used == 1 ? "1 asset uses" : $"{used} assets use")} it (disposed ones included). Archive it instead.");
        db.AssetTypes.Remove(type); // its properties go with it
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Deleted, type.DepartmentId, type.Name, new { type.Name });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- properties

    public async Task<AssetTypeProperty> AddPropertyAsync(Guid typeId, AssetPropertyInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var (name, options) = AssetPropertyRules.ValidateDefinition(input.Name, input.PropertyType, input.Options);
        RequireUniquePropertyName(type, name, null);
        var property = new AssetTypeProperty
        {
            AssetTypeId = type.Id,
            Name = name,
            PropertyType = input.PropertyType,
            Options = options,
            IsRequired = input.IsRequired,
            DisplayOrder = type.Properties.Count == 0 ? 1 : type.Properties.Max(p => p.DisplayOrder) + 1
        };
        db.AssetTypeProperties.Add(property);
        type.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Updated, type.DepartmentId, type.Name,
            new { propertyAdded = new { name, type = input.PropertyType, required = input.IsRequired, options } });
        await db.SaveChangesAsync(ct);
        return property;
    }

    /// <summary>Rename, change required, edit the options, or change the type - the last two only as far as §6.19 allows while values exist.</summary>
    public async Task<AssetTypeProperty> UpdatePropertyAsync(Guid typeId, Guid propertyId, AssetPropertyInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var property = type.Properties.FirstOrDefault(p => p.Id == propertyId) ?? throw new NotFoundException("Property not found.");
        var (name, options) = AssetPropertyRules.ValidateDefinition(input.Name, input.PropertyType, input.Options);
        RequireUniquePropertyName(type, name, propertyId);
        AssetPropertyRules.CheckChange(property, input.PropertyType, options, await ValueCountsAsync(propertyId, ct));

        var changes = new ChangeSet()
            .Track("name", property.Name, name)
            .Track("type", property.PropertyType, input.PropertyType)
            .Track("required", property.IsRequired, input.IsRequired)
            .Track("options", string.Join(" | ", property.Options), string.Join(" | ", options));
        if (!changes.HasChanges) return property;
        property.Name = name;
        property.PropertyType = input.PropertyType;
        property.Options = options;
        property.IsRequired = input.IsRequired;
        type.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Updated, type.DepartmentId, type.Name,
            new { propertyChanged = property.Name, changes = changes.Changes });
        await db.SaveChangesAsync(ct);
        return property;
    }

    /// <summary>Delete a property; the values assets hold for it go with it (the page confirms the count first). Returns how many values went.</summary>
    public async Task<int> DeletePropertyAsync(Guid typeId, Guid propertyId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var property = type.Properties.FirstOrDefault(p => p.Id == propertyId) ?? throw new NotFoundException("Property not found.");
        var values = await db.AssetPropertyValues.CountAsync(v => v.AssetTypePropertyId == propertyId, ct);
        db.AssetTypeProperties.Remove(property); // its values cascade
        type.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.AssetType, type.Id, AuditAction.Updated, type.DepartmentId, type.Name,
            new { propertyRemoved = property.Name, valuesDeleted = values });
        await db.SaveChangesAsync(ct);
        return values;
    }

    /// <summary>Move a property one place up (-1) or down (+1) in the display order.</summary>
    public async Task MovePropertyAsync(Guid typeId, Guid propertyId, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var type = await db.AssetTypes.Include(t => t.Properties).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new NotFoundException("Asset type not found.");
        RequireConfigure(actor, type.DepartmentId);
        var ordered = type.Properties.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToList();
        var index = ordered.FindIndex(p => p.Id == propertyId);
        if (index < 0) throw new NotFoundException("Property not found.");
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= ordered.Count) return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].DisplayOrder = i + 1;
        type.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private static IQueryable<AssetType> Load(IQueryable<AssetType> q) => q.Include(t => t.Department).Include(t => t.Properties);

    private static void RequireConfigure(Actor actor, Guid departmentId) =>
        AccessPolicy.Require(AccessPolicy.CanConfigureAssetsIn(actor, departmentId), "You don't have permission to configure this department's asset types.");

    private static string RequireName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("A name is required.");
        if (n.Length > 100) throw new ValidationException("The name must be 100 characters or fewer.");
        return n;
    }

    private async Task RequireUniqueNameAsync(Guid departmentId, string name, Guid? exceptId, CancellationToken ct)
    {
        var n = name.ToLowerInvariant();
        if (await db.AssetTypes.AnyAsync(t => t.DepartmentId == departmentId && t.Name.ToLower() == n && t.Id != exceptId, ct))
            throw new ValidationException($"The department already has an asset type called \"{name}\".");
    }

    private static void RequireUniquePropertyName(AssetType type, string name, Guid? exceptId)
    {
        if (type.Properties.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"\"{type.Name}\" already has a property called \"{name}\".");
    }

    /// <summary>Blank or 0 = no scheduled checks; otherwise 1 to 3,650 days.</summary>
    public static int? CleanInterval(int? days)
    {
        if (days is null or 0) return null;
        if (days < 0 || days > MaxCheckIntervalDays)
            throw new ValidationException($"The check interval must be between 1 and {MaxCheckIntervalDays} days, or blank for none.");
        return days;
    }
}

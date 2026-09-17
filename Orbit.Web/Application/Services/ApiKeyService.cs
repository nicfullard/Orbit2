using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

public sealed class ApiKeyService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        return await db.ApiKeys.AsNoTracking().Include(k => k.Department)
            .OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.CreatedAt).ToListAsync(ct);
    }

    /// <summary>Creates a key. The raw value is returned exactly once and never stored.</summary>
    public async Task<CreatedApiKey> CreateAsync(ApiKeyInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ValidationException("Name is required.");
        if (input.Role != OrbitRole.SystemAdmin && input.DepartmentId is null)
            throw new ValidationException("Member and Department Admin keys must be scoped to a department.");
        Guid? departmentId = input.Role == OrbitRole.SystemAdmin ? null : input.DepartmentId;
        if (departmentId is Guid d)
        {
            var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d, ct)
                ?? throw new NotFoundException("Department not found.");
            if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        }

        var raw = ApiKeyHasher.GenerateRawKey();
        var key = new ApiKey
        {
            Name = name,
            HashedKey = ApiKeyHasher.Hash(raw),
            Prefix = raw[..Math.Min(12, raw.Length)],
            Role = input.Role,
            DepartmentId = departmentId,
            CreatedById = actor.UserId,
            CreatedAt = DateTime.UtcNow
        };
        db.ApiKeys.Add(key);
        audit.Add(actor, AuditEntity.ApiKey, key.Id, AuditAction.Created, departmentId, key.Name, new { key.Role, key.DepartmentId, key.Prefix });
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key, raw);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("API key not found.");
        if (key.RevokedAt is not null) return;
        key.RevokedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.ApiKey, key.Id, AuditAction.Revoked, key.DepartmentId, key.Name);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.IsSystemAdmin, "Only a System Admin can manage API keys.");
        return actor;
    }
}

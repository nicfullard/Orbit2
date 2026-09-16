using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

public sealed class AuditService(ApplicationDbContext db, IActorProvider actors)
{
    /// <summary>Queues an audit row on the current unit of work; the caller's SaveChanges persists it atomically with the change.</summary>
    public AuditLog Add(Actor actor, string entityType, Guid entityId, string action, Guid? departmentId, string? summary, object? details = null)
    {
        var log = new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ActorType = actor.Type,
            ActorId = actor.ActorId,
            ActorName = actor.DisplayName,
            DepartmentId = departmentId,
            Summary = summary is { Length: > 500 } ? summary[..500] : summary,
            Details = details is null ? "{}" : JsonSerializer.Serialize(details, OrbitJson.Options),
            Timestamp = DateTime.UtcNow
        };
        db.AuditLogs.Add(log);
        return log;
    }

    public async Task<PagedResult<AuditLog>> ListAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var to = AsUtc(filter.To) ?? DateTime.UtcNow;
        var from = AsUtc(filter.From) ?? to.AddHours(-24);

        var q = db.AuditLogs.AsNoTracking().Where(a => a.Timestamp >= from && a.Timestamp <= to);

        if (!actor.IsSystemAdmin)
            q = q.Where(a => a.DepartmentId == actor.DepartmentId);
        else if (filter.DepartmentId is Guid dept)
            q = q.Where(a => a.DepartmentId == dept);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
            q = q.Where(a => a.EntityType == filter.EntityType);
        if (filter.EntityId is Guid entityId)
            q = q.Where(a => a.EntityId == entityId);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 500);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AuditLog>(items, page, pageSize, total);
    }

    public static DateTime? AsUtc(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value
        : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime()
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

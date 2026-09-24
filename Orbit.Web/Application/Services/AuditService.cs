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

    /// <summary>
    /// The log within the caller's audit.view scope (§6.5). One task's or project's own activity is instead open to
    /// whoever may see that task or project, since it is part of viewing it (the task page, get_task callers).
    /// </summary>
    public async Task<PagedResult<AuditLog>> ListAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var to = AsUtc(filter.To) ?? DateTime.UtcNow;
        var from = AsUtc(filter.From) ?? to.AddHours(-24);

        var q = db.AuditLogs.AsNoTracking().Where(a => a.Timestamp >= from && a.Timestamp <= to);

        if (!(filter.EntityId is Guid id && await CanViewEntityAsync(actor, id, ct)))
        {
            q = Scoping.Audit(q, actor);
            if (filter.DepartmentId is Guid dept)
                q = q.Where(a => a.DepartmentId == dept);
        }

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

    /// <summary>A task or project the actor may see: its activity comes with it, whatever their audit.view scope.</summary>
    private async Task<bool> CanViewEntityAsync(Actor actor, Guid entityId, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == entityId, ct);
        if (task is not null) return AccessPolicy.CanViewTask(actor, task);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entityId, ct);
        if (project is null) return false;
        var shared = actor.DepartmentId is Guid d && await db.Tasks.AnyAsync(t => t.ProjectId == project.Id && t.DepartmentId == d, ct);
        return AccessPolicy.CanViewProject(actor, project, shared);
    }

    public static DateTime? AsUtc(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value
        : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime()
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

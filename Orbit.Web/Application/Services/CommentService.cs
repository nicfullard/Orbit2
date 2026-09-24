using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Comments on tasks and on assets (spec §6.19). Whoever can see the task or asset can read and add them.</summary>
public sealed class CommentService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    public async Task<IReadOnlyList<Comment>> ListAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        return await db.Comments.AsNoTracking().Include(c => c.Author)
            .Where(c => c.TaskId == taskId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<Comment> AddAsync(Guid taskId, string body, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var text = CleanBody(body);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");

        var comment = new Comment { TaskId = taskId, AuthorId = actor.UserId, Body = text, CreatedAt = DateTime.UtcNow };
        db.Comments.Add(comment);
        task.UpdatedAt = comment.CreatedAt;
        audit.Add(actor, AuditEntity.Task, taskId, AuditAction.CommentAdded, task.DepartmentId, task.Title, Details(comment));
        await db.SaveChangesAsync(ct);

        await db.Entry(comment).Reference(c => c.Author).LoadAsync(ct);
        return comment;
    }

    /// <summary>An asset's comments, oldest first: whoever can see the asset (assets.view) can read them.</summary>
    public async Task<IReadOnlyList<Comment>> ListForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var asset = await db.Assets.AsNoTracking().Include(a => a.Assignments).FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanViewAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), "You don't have permission to see this asset.");
        return await db.Comments.AsNoTracking().Include(c => c.Author)
            .Where(c => c.AssetId == assetId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
    }

    /// <summary>Comment on an asset: follows viewing it, as on tasks - so the holder can report a problem. Disposed assets take comments too.</summary>
    public async Task<Comment> AddToAssetAsync(Guid assetId, string body, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var text = CleanBody(body);
        var asset = await db.Assets.Include(a => a.Assignments).FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new NotFoundException("Asset not found.");
        AccessPolicy.Require(AccessPolicy.CanCommentOnAsset(actor, asset, AccessPolicy.IsAssigned(actor, asset)), "You don't have permission to see this asset.");

        var comment = new Comment { AssetId = assetId, AuthorId = actor.UserId, Body = text, CreatedAt = DateTime.UtcNow };
        db.Comments.Add(comment);
        asset.UpdatedAt = comment.CreatedAt;
        audit.Add(actor, AuditEntity.Asset, assetId, AuditAction.CommentAdded, asset.DepartmentId, AssetService.Summary(asset), Details(comment));
        await db.SaveChangesAsync(ct);

        await db.Entry(comment).Reference(c => c.Author).LoadAsync(ct);
        return comment;
    }

    private static string CleanBody(string? body)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ValidationException("Comment text is required.");
        if (text.Length > 10_000) throw new ValidationException("Comment must be 10,000 characters or fewer.");
        return text;
    }

    private static object Details(Comment c) =>
        new { commentId = c.Id, body = c.Body.Length > 500 ? c.Body[..500] + "..." : c.Body };
}

using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

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
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ValidationException("Comment text is required.");
        if (text.Length > 10_000) throw new ValidationException("Comment must be 10,000 characters or fewer.");

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");

        var comment = new Comment { TaskId = taskId, AuthorId = actor.UserId, Body = text, CreatedAt = DateTime.UtcNow };
        db.Comments.Add(comment);
        task.UpdatedAt = comment.CreatedAt;
        audit.Add(actor, AuditEntity.Task, taskId, AuditAction.CommentAdded, task.DepartmentId, task.Title,
            new { commentId = comment.Id, body = text.Length > 500 ? text[..500] + "..." : text });
        await db.SaveChangesAsync(ct);

        await db.Entry(comment).Reference(c => c.Author).LoadAsync(ct);
        return comment;
    }
}

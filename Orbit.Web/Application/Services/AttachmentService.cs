using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Files attached to tasks and projects (spec §6.18). Metadata is a row; the bytes go through <see cref="AttachmentStorage"/>.
/// Attaching follows the commenting rule (anyone who can see the task, or the project's own department); removing takes the
/// uploader or someone who may edit the parent. Every upload and removal is audited on the parent.
/// </summary>
public sealed class AttachmentService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    AttachmentStorage storage,
    IOptions<AttachmentOptions> options,
    ILogger<AttachmentService> logger)
{
    private AttachmentOptions Limits => options.Value;

    public async Task<IReadOnlyList<Attachment>> ListForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        return await Ordered(db.Attachments.AsNoTracking().Where(a => a.TaskId == taskId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Attachment>> ListForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project not found.");
        await RequireCanViewProjectAsync(actor, project, ct);
        return await Ordered(db.Attachments.AsNoTracking().Where(a => a.ProjectId == projectId)).ToListAsync(ct);
    }

    public async Task<Attachment> AddToTaskAsync(Guid taskId, AttachmentUpload upload, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException("Task not found.");
        AccessPolicy.Require(AccessPolicy.CanAttachToTask(actor, task), "This task belongs to another department.");
        var attachment = await StoreAsync(actor, upload, a => a.TaskId = taskId, ct);
        task.UpdatedAt = attachment.UploadedAt;
        audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.AttachmentAdded, task.DepartmentId, task.Title, Details(attachment));
        await CommitAsync(attachment, ct);
        return attachment;
    }

    public async Task<Attachment> AddToProjectAsync(Guid projectId, AttachmentUpload upload, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project not found.");
        AccessPolicy.Require(AccessPolicy.CanAttachToProject(actor, project),
            "Only the project's own department can attach files to it.");
        if (project.Status == ProjectStatus.Archived) throw new ValidationException("Files can't be attached to an archived project.");
        var attachment = await StoreAsync(actor, upload, a => a.ProjectId = projectId, ct);
        project.UpdatedAt = attachment.UploadedAt;
        audit.Add(actor, AuditEntity.Project, project.Id, AuditAction.AttachmentAdded, project.DepartmentId, project.Name, Details(attachment));
        await CommitAsync(attachment, ct);
        return attachment;
    }

    /// <summary>The attachment and a stream over its bytes, for download. The caller disposes the stream.</summary>
    public async Task<(Attachment Attachment, Stream Content)> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var attachment = await Load(db.Attachments.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Attachment not found.");
        await RequireCanViewParentAsync(actor, attachment, ct);
        if (!storage.Exists(attachment.StoragePath))
        {
            logger.LogError("Attachment {AttachmentId} ({FileName}) is missing from storage at {Path}", attachment.Id, attachment.FileName, attachment.StoragePath);
            throw new NotFoundException("The attachment's file is missing from storage.");
        }
        return (attachment, storage.Open(attachment.StoragePath));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var attachment = await Load(db.Attachments).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Attachment not found.");
        var allowed = attachment.Task is not null
            ? AccessPolicy.CanDeleteAttachment(actor, attachment, attachment.Task)
            : AccessPolicy.CanDeleteAttachment(actor, attachment, attachment.Project!);
        AccessPolicy.Require(allowed, "Only the uploader, or someone who may edit what it is attached to, can delete an attachment.");

        if (attachment.Task is TaskItem task)
        {
            task.UpdatedAt = DateTime.UtcNow;
            audit.Add(actor, AuditEntity.Task, task.Id, AuditAction.AttachmentRemoved, task.DepartmentId, task.Title, Details(attachment));
        }
        else
        {
            var project = attachment.Project!;
            project.UpdatedAt = DateTime.UtcNow;
            audit.Add(actor, AuditEntity.Project, project.Id, AuditAction.AttachmentRemoved, project.DepartmentId, project.Name, Details(attachment));
        }
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        // The row is gone; a file left behind is only wasted space, so a failure here is logged, not surfaced.
        try { storage.Delete(attachment.StoragePath); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not delete the file for attachment {AttachmentId} at {Path}", attachment.Id, attachment.StoragePath); }
    }

    /// <summary>
    /// Reduce an uploaded name to a plain file name: no directories, no control or reserved characters, never empty,
    /// at most 255 characters (the extension is kept when the name has to be cut).
    /// </summary>
    public static string CleanFileName(string? name)
    {
        const string ReservedNameChars = "<>:\"/\\|?*";
        var trimmed = (name ?? string.Empty).Replace('\\', '/');
        trimmed = trimmed[(trimmed.LastIndexOf('/') + 1)..].Trim();
        // The same reserved set on every platform (Windows' list), so a name is cleaned identically wherever Orbit runs.
        var clean = new string(trimmed.Where(c => !char.IsControl(c) && ReservedNameChars.IndexOf(c) < 0).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(clean)) return "attachment";
        if (clean.Length <= 255) return clean;
        var ext = Path.GetExtension(clean);
        if (ext.Length > 20) ext = string.Empty;
        return clean[..(255 - ext.Length)] + ext;
    }

    // --- helpers -------------------------------------------------------------------------

    private async Task<Attachment> StoreAsync(Actor actor, AttachmentUpload upload, Action<Attachment> attach, CancellationToken ct)
    {
        var fileName = CleanFileName(upload.FileName);
        if (upload.SizeBytes <= 0) throw new ValidationException($"\"{fileName}\" is empty.");
        if (upload.SizeBytes > Limits.MaxFileSizeBytes)
            throw new ValidationException($"\"{fileName}\" is larger than the {Limits.MaxFileSizeMb} MB limit.");

        var attachment = new Attachment
        {
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(upload.ContentType) || upload.ContentType.Length > 200
                ? "application/octet-stream"
                : upload.ContentType.Trim(),
            SizeBytes = upload.SizeBytes,
            UploadedById = actor.UserId,
            UploadedAt = DateTime.UtcNow
        };
        attach(attachment);
        attachment.StoragePath = await storage.SaveAsync(attachment.Id, upload.Content, ct);
        db.Attachments.Add(attachment);
        return attachment;
    }

    /// <summary>Save the row; if that fails the file just written must not be left orphaned.</summary>
    private async Task CommitAsync(Attachment attachment, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            try { storage.Delete(attachment.StoragePath); } catch { /* best effort */ }
            throw;
        }
        await db.Entry(attachment).Reference(a => a.UploadedBy).LoadAsync(ct);
    }

    private static object Details(Attachment a) => new { attachmentId = a.Id, fileName = a.FileName, sizeBytes = a.SizeBytes };

    private static IQueryable<Attachment> Load(IQueryable<Attachment> q) => q
        .Include(a => a.UploadedBy)
        .Include(a => a.Task)
        .Include(a => a.Project);

    private static IOrderedQueryable<Attachment> Ordered(IQueryable<Attachment> q) =>
        q.Include(a => a.UploadedBy).OrderBy(a => a.UploadedAt);

    private async Task RequireCanViewParentAsync(Actor actor, Attachment attachment, CancellationToken ct)
    {
        if (attachment.Task is TaskItem task)
            AccessPolicy.Require(AccessPolicy.CanViewTask(actor, task), "This task belongs to another department.");
        else
            await RequireCanViewProjectAsync(actor, attachment.Project!, ct);
    }

    /// <summary>A department that has tasks on another department's project sees the project - and its attachments - read-only (§6.2.1).</summary>
    private async Task RequireCanViewProjectAsync(Actor actor, Project project, CancellationToken ct)
    {
        var shared = actor.DepartmentId is Guid d && await db.Tasks.AnyAsync(t => t.ProjectId == project.Id && t.DepartmentId == d, ct);
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project, shared), "This project belongs to another department.");
    }
}

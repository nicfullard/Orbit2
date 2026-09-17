using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

public sealed class DepartmentService(ApplicationDbContext db, IActorProvider actors, AuditService audit, UserDirectoryService users)
{
    /// <summary>Read-only listing, available to every role (pickers, filters, the list_departments tool).</summary>
    public async Task<IReadOnlyList<Department>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        var q = db.Departments.AsNoTracking().AsQueryable();
        if (!includeArchived) q = q.Where(d => !d.IsArchived);
        return await q.OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Department> GetAsync(Guid id, CancellationToken ct = default)
    {
        await actors.GetAsync(ct);
        return await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Department not found.");
    }

    public async Task<IReadOnlyList<DepartmentOverview>> GetOverviewAsync(CancellationToken ct = default)
    {
        await RequireManagerAsync(ct);
        return await db.Departments.AsNoTracking().OrderBy(d => d.IsArchived).ThenBy(d => d.Name)
            .Select(d => new DepartmentOverview(
                d,
                d.Users.Count(u => u.IsActive && !u.IsSystemAccount),
                d.Projects.Count(p => p.Status == ProjectStatus.Active),
                d.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)))
            .ToListAsync(ct);
    }

    public async Task<DepartmentDetail> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await RequireManagerAsync(ct);
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Department not found.");
        var members = await users.ListAsync(null, id, includeInactive: true, ct);
        var projects = await db.Projects.AsNoTracking().Include(p => p.Owner).Include(p => p.Department)
            .Where(p => p.DepartmentId == id).OrderBy(p => p.Status).ThenBy(p => p.Name)
            .Select(p => new
            {
                Project = p,
                Total = p.Tasks.Count(),
                Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
                Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
            }).ToListAsync(ct);
        var open = await db.Tasks.CountAsync(t => t.DepartmentId == id && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled, ct);
        var total = await db.Tasks.CountAsync(t => t.DepartmentId == id, ct);
        return new DepartmentDetail(dept, members,
            projects.Select(r => new ProjectListItem(r.Project, r.Total, r.Open, r.Done)).ToList(), open, total);
    }

    public async Task<Department> CreateAsync(string name, string? description, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var n = RequireName(name);
        if (await db.Departments.AnyAsync(d => d.Name == n, ct))
            throw new ValidationException($"A department named \"{n}\" already exists.");
        var dept = new Department { Name = n, Description = Clean(description), CreatedAt = DateTime.UtcNow };
        db.Departments.Add(dept);
        audit.Add(actor, AuditEntity.Department, dept.Id, AuditAction.Created, dept.Id, dept.Name, new { dept.Name });
        await db.SaveChangesAsync(ct);
        return dept;
    }

    public async Task<Department> UpdateAsync(Guid id, string name, string? description, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var n = RequireName(name);
        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Department not found.");
        if (await db.Departments.AnyAsync(d => d.Name == n && d.Id != id, ct))
            throw new ValidationException($"A department named \"{n}\" already exists.");
        var changes = new ChangeSet().TrackText("name", dept.Name, n).TrackText("description", dept.Description, description);
        if (!changes.HasChanges) return dept;
        dept.Name = n;
        dept.Description = Clean(description);
        audit.Add(actor, AuditEntity.Department, dept.Id, AuditAction.Updated, dept.Id, dept.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return dept;
    }

    /// <summary>Soft archive: nothing cascades; the department just stops being offered for new work.</summary>
    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await RequireManagerAsync(ct);
        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived == archived) return;
        dept.IsArchived = archived;
        dept.ArchivedAt = archived ? DateTime.UtcNow : null;
        audit.Add(actor, AuditEntity.Department, dept.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, dept.Id, dept.Name);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Actor> RequireManagerAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageDepartments(actor), "Only a System Admin can manage departments.");
        return actor;
    }

    private static string RequireName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("Name is required.");
        if (n.Length > 200) throw new ValidationException("Name must be 200 characters or fewer.");
        return n;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

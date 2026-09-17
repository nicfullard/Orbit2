using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Projects;

public sealed class ProjectForm
{
    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public Guid? OwnerId { get; set; }
    [DataType(DataType.Date)] public DateOnly? TargetDate { get; set; }

    public ProjectInput ToInput() => new()
    {
        Name = Name, Description = Description, DepartmentId = DepartmentId, Status = Status, OwnerId = OwnerId, TargetDate = TargetDate
    };

    public static ProjectForm From(Project p) => new()
    {
        Name = p.Name, Description = p.Description, DepartmentId = p.DepartmentId, Status = p.Status, OwnerId = p.OwnerId, TargetDate = p.TargetDate
    };
}

public sealed class ProjectFormLookups
{
    public required Actor Actor { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    public IReadOnlyList<SelectListItem> Owners { get; init; } = [];

    public static async Task<ProjectFormLookups> BuildAsync(Actor actor, DepartmentService departments, UserDirectoryService users, ProjectForm form, CancellationToken ct)
    {
        var deptItems = new List<SelectListItem>();
        if (actor.IsSystemAdmin)
        {
            deptItems.Add(new SelectListItem("- choose -", string.Empty));
            deptItems.AddRange((await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }
        var people = actor.IsSystemAdmin
            ? await users.ListAsync(null, null, false, ct)
            : await users.GetAssignableAsync(actor.DepartmentId!.Value, ct);
        var owners = people.Select(u => new SelectListItem(
            actor.IsSystemAdmin ? $"{u.DisplayName}{(u.DepartmentName is null ? " (System Admin)" : $" ({u.DepartmentName})")}" : u.DisplayName,
            u.Id.ToString(), u.Id == form.OwnerId)).ToList();
        return new ProjectFormLookups { Actor = actor, Departments = deptItems, Owners = owners };
    }
}

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
    /// <summary>Required project buffer in whole working days (§6.17); blank or 0 = none.</summary>
    [Display(Name = "Required project buffer"), Range(0, 260)] public int? RequiredBufferWorkingDays { get; set; }

    public ProjectInput ToInput() => new()
    {
        Name = Name, Description = Description, DepartmentId = DepartmentId, Status = Status, OwnerId = OwnerId, TargetDate = TargetDate,
        RequiredBufferWorkingDays = RequiredBufferWorkingDays
    };

    public static ProjectForm From(Project p) => new()
    {
        Name = p.Name, Description = p.Description, DepartmentId = p.DepartmentId, Status = p.Status, OwnerId = p.OwnerId, TargetDate = p.TargetDate,
        RequiredBufferWorkingDays = p.RequiredBufferWorkingDays
    };
}

public sealed class ProjectFormLookups
{
    public required Actor Actor { get; init; }
    /// <summary>The department picker: creating anywhere on the create form, moving the project (projects.edit at All) on the edit form.</summary>
    public bool CanChooseDepartment { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    public IReadOnlyList<SelectListItem> Owners { get; init; } = [];

    public static async Task<ProjectFormLookups> BuildAsync(Actor actor, DepartmentService departments, UserDirectoryService users, ProjectForm form, bool canChooseDepartment, CancellationToken ct)
    {
        var deptItems = new List<SelectListItem>();
        if (canChooseDepartment)
        {
            deptItems.Add(new SelectListItem("- choose -", string.Empty));
            deptItems.AddRange((await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }
        var seesEverywhere = actor.CanAnywhere(Permission.TasksView);
        var people = seesEverywhere
            ? await users.ListAsync(null, null, false, ct)
            : actor.DepartmentId is Guid ownDept ? await users.GetAssignableAsync(ownDept, ct) : [];
        var owners = people.Select(u => new SelectListItem(
            seesEverywhere ? $"{u.DisplayName} ({u.DepartmentName ?? u.Role.Name})" : u.DisplayName,
            u.Id.ToString(), u.Id == form.OwnerId)).ToList();
        return new ProjectFormLookups { Actor = actor, CanChooseDepartment = canChooseDepartment, Departments = deptItems, Owners = owners };
    }
}

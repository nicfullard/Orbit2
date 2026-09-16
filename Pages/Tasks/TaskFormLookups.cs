using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

/// <summary>A picker option that also carries the department it belongs to, so the form can react client-side.</summary>
public sealed record DepartmentedOption(string Value, string Text, Guid? DepartmentId, bool Selected);

/// <summary>Select-list data for the task create/edit forms, built for the current actor.</summary>
public sealed class TaskFormLookups
{
    public required Actor Actor { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    /// <summary>Projects the actor may file the task under (plus the one it is already on). Each carries its department.</summary>
    public IReadOnlyList<DepartmentedOption> Projects { get; init; } = [];
    /// <summary>Assignable users. DepartmentId is null for System Admins, who can be assigned anywhere.</summary>
    public IReadOnlyList<DepartmentedOption> Assignees { get; init; } = [];
    public IReadOnlyList<SelectListItem> Sprints { get; init; } = [];

    public static async Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        TaskForm form,
        CancellationToken ct)
    {
        var deptItems = new List<SelectListItem>();
        if (actor.IsSystemAdmin)
        {
            deptItems.Add(new SelectListItem("(default)", string.Empty, form.DepartmentId is null));
            deptItems.AddRange((await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }

        var projectItems = new List<DepartmentedOption> { new(string.Empty, "(standalone task)", null, form.ProjectId is null) };
        projectItems.AddRange((await projects.ListOpenForPickerAsync(null, form.ProjectId, ct))
            .Select(p => new DepartmentedOption(p.Id.ToString(),
                actor.IsSystemAdmin ? $"{p.Department.Name} / {p.Name}" : p.Name,
                p.DepartmentId, p.Id == form.ProjectId)));

        var assigneeItems = new List<DepartmentedOption> { new(string.Empty, "(unassigned)", null, form.AssigneeId is null) };
        var candidates = actor.IsSystemAdmin
            ? await users.ListAsync(null, null, false, ct)
            : await users.GetAssignableAsync(actor.DepartmentId!.Value, ct);
        assigneeItems.AddRange(candidates.Select(u => new DepartmentedOption(
            u.Id.ToString(),
            actor.IsSystemAdmin
                ? $"{u.DisplayName} ({(u.Role == OrbitRole.SystemAdmin ? "System Admin" : u.DepartmentName)})"
                : u.DisplayName,
            u.Role == OrbitRole.SystemAdmin ? null : u.DepartmentId,
            u.Id == form.AssigneeId)));

        var sprintItems = new List<SelectListItem> { new("(backlog)", string.Empty) };
        sprintItems.AddRange((await sprints.ListOpenAsync(ct))
            .Select(s => new SelectListItem($"{s.Name} ({s.Status})", s.Id.ToString(), s.Id == form.SprintId)));

        return new TaskFormLookups
        {
            Actor = actor,
            Departments = deptItems,
            Projects = projectItems,
            Assignees = assigneeItems,
            Sprints = sprintItems
        };
    }

    public IEnumerable<SelectListItem> StatusItems(TaskItem? existing, TaskItemStatus selected)
    {
        var deptId = existing?.DepartmentId ?? Actor.DepartmentId ?? Guid.Empty;
        var canClose = existing is null ? Actor.IsAdminFor(deptId) || Actor.IsSystemAdmin : Actor.IsAdminFor(existing.DepartmentId);
        foreach (var s in Enum.GetValues<TaskItemStatus>())
        {
            var closed = s.IsClosed();
            var allowed = !closed || canClose || (existing is not null && existing.Status == s);
            if (!allowed) continue;
            yield return new SelectListItem(s.Label(), s.ToString(), s == selected);
        }
    }
}

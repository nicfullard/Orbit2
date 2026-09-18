using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
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
    /// <summary>Parent task options (§6.15), each carrying its project and department so the form can filter them client-side.</summary>
    public IReadOnlyList<ParentCandidate> Parents { get; init; } = [];

    /// <summary>Lookups without the Parent task picker - for the recurring-definition form, which shares these fields but has no parent (§6.15).</summary>
    public static Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        TaskForm form,
        CancellationToken ct) =>
        BuildAsync(actor, departments, projects, users, sprints, null, form, null, ct);

    /// <param name="structure">Supplies the Parent task options; null leaves the picker empty.</param>
    /// <param name="existingTaskId">The task being edited, so it is not offered as its own parent.</param>
    public static async Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        TaskStructureService? structure,
        TaskForm form,
        Guid? existingTaskId,
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

        // Open tasks only; a closed parent that the task already sits under is kept so saving doesn't silently detach it.
        var parents = structure is null ? new List<ParentCandidate>() : (await structure.ListParentCandidatesAsync(existingTaskId, ct)).ToList();
        if (structure is not null && form.ParentTaskId is Guid currentParent && parents.All(p => p.Id != currentParent)
            && (await structure.LoadAncestorsAsync(currentParent, ct)).LastOrDefault() is { } current)
        {
            parents.Insert(0, new ParentCandidate(current.Id, current.Title, current.ProjectId, current.Project?.Name, current.DepartmentId, current.Department.Name));
        }

        return new TaskFormLookups
        {
            Actor = actor,
            Departments = deptItems,
            Projects = projectItems,
            Assignees = assigneeItems,
            Sprints = sprintItems,
            Parents = parents
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

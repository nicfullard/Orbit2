using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Assets;
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
    /// <summary>The department picker is offered to whoever may create tasks in every department (§6.2.1); everyone else files in the default department.</summary>
    public bool CanChooseDepartment { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    /// <summary>Projects the actor may file the task under (plus the one it is already on). Each carries its department.</summary>
    public IReadOnlyList<DepartmentedOption> Projects { get; init; } = [];
    /// <summary>Assignable users. DepartmentId is null for users whose role sees every department, who can be assigned anywhere.</summary>
    public IReadOnlyList<DepartmentedOption> Assignees { get; init; } = [];
    public IReadOnlyList<SelectListItem> Sprints { get; init; } = [];
    /// <summary>Parent task options (§6.15), each carrying its project and department so the form can filter them client-side.</summary>
    public IReadOnlyList<ParentCandidate> Parents { get; init; } = [];
    /// <summary>The asset picker is offered to whoever can see assets (assets.view, §6.19); the service checks the asset chosen.</summary>
    public bool CanPickAsset { get; init; }
    /// <summary>The chosen asset's number and name, for the picker's chip - or the read-only line for someone without the picker.</summary>
    public string? SelectedAssetLabel { get; init; }

    /// <summary>The asset a task or recurring task is linked to now, as the builders below take it.</summary>
    public static AssetRef? RefOf(Asset? asset) => asset is null ? null : new AssetRef(asset.Id, asset.AssetNumber, asset.Name);

    /// <summary>Lookups without the Parent task picker - for the recurring-definition form, which shares these fields but has no parent (§6.15).</summary>
    public static Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        AssetService assets,
        TaskForm form,
        AssetRef? currentAsset,
        CancellationToken ct) =>
        BuildAsync(actor, departments, projects, users, sprints, null, assets, form, null, currentAsset, ct);

    /// <param name="structure">Supplies the Parent task options; null leaves the picker empty.</param>
    /// <param name="existingTaskId">The task being edited, so it is not offered as its own parent.</param>
    /// <param name="currentAsset">The asset the task is linked to now, named on the form even when the caller can't see it.</param>
    public static async Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        TaskStructureService? structure,
        AssetService assets,
        TaskForm form,
        Guid? existingTaskId,
        AssetRef? currentAsset,
        CancellationToken ct)
    {
        var canChooseDepartment = actor.CanAnywhere(Permission.TasksCreate);
        var seesEverywhere = actor.CanAnywhere(Permission.TasksView);
        var deptItems = new List<SelectListItem>();
        if (canChooseDepartment)
        {
            deptItems.Add(new SelectListItem("(default)", string.Empty, form.DepartmentId is null));
            deptItems.AddRange((await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }

        var projectItems = new List<DepartmentedOption> { new(string.Empty, "(standalone task)", null, form.ProjectId is null) };
        projectItems.AddRange((await projects.ListOpenForPickerAsync(null, form.ProjectId, ct: ct))
            .Select(p => new DepartmentedOption(p.Id.ToString(),
                seesEverywhere ? $"{p.Department.Name} / {p.Name}" : p.Name,
                p.DepartmentId, p.Id == form.ProjectId)));

        var assigneeItems = new List<DepartmentedOption> { new(string.Empty, "(unassigned)", null, form.AssigneeId is null) };
        var candidates = seesEverywhere
            ? await users.ListAsync(null, null, false, ct)
            : actor.DepartmentId is Guid ownDept ? await users.GetAssignableAsync(ownDept, ct) : [];
        assigneeItems.AddRange(candidates.Select(u => new DepartmentedOption(
            u.Id.ToString(),
            seesEverywhere ? $"{u.DisplayName} ({u.DepartmentName ?? u.Role.Name})" : u.DisplayName,
            u.CanViewAllTasks ? null : u.DepartmentId,
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

        // The chip names the asset chosen: the current link whoever can see it, or another asset the caller can see.
        string? assetLabel = null;
        if (form.AssetId is Guid assetId)
        {
            var chosen = currentAsset?.Id == assetId ? currentAsset : await assets.GetRefAsync(assetId, ct);
            assetLabel = chosen is null ? "(an asset you can't see)" : AssetRules.Label(chosen.AssetNumber, chosen.Name);
        }

        return new TaskFormLookups
        {
            Actor = actor,
            CanChooseDepartment = canChooseDepartment,
            Departments = deptItems,
            Projects = projectItems,
            Assignees = assigneeItems,
            Sprints = sprintItems,
            Parents = parents,
            CanPickAsset = actor.Has(Permission.AssetsView),
            SelectedAssetLabel = assetLabel
        };
    }
}

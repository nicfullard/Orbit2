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
    /// <summary>
    /// The people chosen in the Assignees picker (§6.2.3), for its chips: the saved assignees, or the ones just posted. Anyone
    /// assigned is among them whether or not they could still be picked, so a save never drops a colleague who has since been
    /// deactivated or changed department. The picker searches for the rest (/Tasks/Assignees).
    /// </summary>
    public IReadOnlyList<UserSummary> SelectedAssignees { get; init; } = [];
    /// <summary>The department a task belongs to while the form names neither one nor a project: the caller's own.</summary>
    public Guid? DefaultDepartmentId { get; init; }
    public IReadOnlyList<SelectListItem> Sprints { get; init; } = [];
    /// <summary>Parent task options (§6.15), each carrying its project and department so the form can filter them client-side.</summary>
    public IReadOnlyList<ParentCandidate> Parents { get; init; } = [];
    /// <summary>The asset picker is offered to whoever can see assets (assets.view, §6.19); the service checks the asset chosen.</summary>
    public bool CanPickAsset { get; init; }
    /// <summary>The chosen asset's number and name, for the picker's chip - or the read-only line for someone without the picker.</summary>
    public string? SelectedAssetLabel { get; init; }
    /// <summary>Whether the form carries the Requestee field (§6.2.2): the task forms do, the recurring form doesn't.</summary>
    public bool OfferRequestee { get; init; }
    /// <summary>Whether the caller may change the requestee here (<see cref="CanChangeRequestee"/>); otherwise it is shown and kept as it is.</summary>
    public bool CanChooseRequestee { get; init; }
    /// <summary>The people the task may be created for, "(nobody else)" first; the current requestee is always among them.</summary>
    public IReadOnlyList<SelectListItem> Requestees { get; init; } = [];
    /// <summary>The current requestee's name, for the read-only line when the caller can't change it.</summary>
    public string? RequesteeLabel { get; init; }

    /// <summary>The asset a task or recurring task is linked to now, as the builders below take it.</summary>
    public static AssetRef? RefOf(Asset? asset) => asset is null ? null : new AssetRef(asset.Id, asset.AssetNumber, asset.Name);

    /// <summary>
    /// Whether the caller may set or change a task's requestee (§6.2.2): tasks.create_for above Own, and reaching the current
    /// requestee too - a requestee set by someone with a wider reach is shown and kept, not offered for change.
    /// </summary>
    public static bool CanChangeRequestee(Actor actor, ApplicationUser? current) =>
        AccessPolicy.CanCreateTasksForOthers(actor)
        && (current is null || AccessPolicy.CanCreateTaskFor(actor, current.Id, current.DepartmentId));

    private static string PersonName(ApplicationUser u) => u.IsActive ? u.DisplayName : $"{u.DisplayName} (deactivated)";

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
        BuildAsync(actor, departments, projects, users, sprints, null, assets, form, null, currentAsset, false, null, ct);

    /// <param name="structure">Supplies the Parent task options; null leaves the picker empty.</param>
    /// <param name="existingTaskId">The task being edited, so it is not offered as its own parent.</param>
    /// <param name="currentAsset">The asset the task is linked to now, named on the form even when the caller can't see it.</param>
    /// <param name="offerRequestee">Whether the form carries the Requestee field (§6.2.2).</param>
    /// <param name="currentRequestee">The task's saved requestee, kept on the form even when deactivated or out of the caller's reach.</param>
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
        bool offerRequestee,
        ApplicationUser? currentRequestee,
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

        var selectedAssignees = await users.FindManyAsync(form.AssigneeIds, ct);

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

        // The requestee (§6.2.2): whom the caller may create tasks for, plus the saved one so a save never drops them silently.
        var canChooseRequestee = offerRequestee && CanChangeRequestee(actor, currentRequestee);
        var requesteeItems = new List<SelectListItem>();
        if (canChooseRequestee)
        {
            var everywhere = actor.CanAnywhere(Permission.TasksCreateFor);
            requesteeItems.Add(new SelectListItem("(nobody else)", string.Empty, form.RequesteeId is null));
            var people = await users.PeopleInReachAsync(Permission.TasksCreateFor, ct);
            requesteeItems.AddRange(people.Select(u => new SelectListItem(
                (everywhere ? $"{u.DisplayName} ({u.DepartmentName ?? u.Role.Name})" : u.DisplayName) + (u.Id == actor.UserId ? " (you)" : ""),
                u.Id.ToString(), u.Id == form.RequesteeId)));
            if (currentRequestee is not null && people.All(p => p.Id != currentRequestee.Id))
                requesteeItems.Insert(1, new SelectListItem(PersonName(currentRequestee), currentRequestee.Id.ToString(), currentRequestee.Id == form.RequesteeId));
        }

        return new TaskFormLookups
        {
            Actor = actor,
            CanChooseDepartment = canChooseDepartment,
            Departments = deptItems,
            Projects = projectItems,
            SelectedAssignees = selectedAssignees,
            DefaultDepartmentId = actor.DepartmentId,
            Sprints = sprintItems,
            Parents = parents,
            CanPickAsset = actor.Has(Permission.AssetsView),
            SelectedAssetLabel = assetLabel,
            OfferRequestee = offerRequestee,
            CanChooseRequestee = canChooseRequestee,
            Requestees = requesteeItems,
            RequesteeLabel = currentRequestee is null ? null : PersonName(currentRequestee)
        };
    }
}

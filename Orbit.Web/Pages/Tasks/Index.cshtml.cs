using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

public class IndexModel(
    TaskService tasks,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    TaskStructureService structure) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public TaskFilter Filter { get; set; } = new();
    /// <summary>"Reset": forget the remembered filter and show the plain list.</summary>
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }

    /// <summary>
    /// The filter fields remembered for the session (§6.2). The page number and the one-off "subtasks of one task"
    /// drill-down are not: a drill-down link neither replaces nor is replaced by the remembered filter.
    /// </summary>
    public static readonly string[] RememberedFilters =
    [
        nameof(TaskFilter.Search), nameof(TaskFilter.DepartmentId), nameof(TaskFilter.ProjectId), nameof(TaskFilter.Status),
        nameof(TaskFilter.AssigneeId), nameof(TaskFilter.Unassigned), nameof(TaskFilter.Priority), nameof(TaskFilter.Type),
        nameof(TaskFilter.Source), nameof(TaskFilter.DueAfter), nameof(TaskFilter.DueBefore), nameof(TaskFilter.OpenOnly),
        nameof(TaskFilter.PlannedToday), nameof(TaskFilter.PlannedFor)
    ];
    private const string MemoryKey = "tasks";

    public Actor Actor { get; private set; } = null!;
    public PagedResult<TaskItem> Result { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> AssigneeItems { get; private set; } = [];
    /// <summary>Candidates for the inline assignee control: everyone the actor may assign to, across all listed departments.</summary>
    public IReadOnlyList<UserSummary> QuickEditAssignees { get; private set; } = [];
    /// <summary>Tasks on this page whose next move is gated by a dependency (§6.15).</summary>
    public IReadOnlyDictionary<Guid, WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, WaitingSummary>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        if (Reset)
        {
            FilterMemory.Forget(Response, MemoryKey);
            return RedirectToPage();
        }
        if (FilterMemory.IsExplicit(Request, RememberedFilters))
            FilterMemory.Remember(Request, Response, MemoryKey, Actor.UserId, RememberedFilters);
        else if (Filter.ParentTaskId is null && FilterMemory.Recall(Request, MemoryKey, Actor.UserId) is string remembered)
            return LocalRedirect(Request.Path + remembered);

        Filter.AllDepartments = false;
        Filter.BacklogOnly = false;
        Filter.PageSize = 50;
        Result = await tasks.ListAsync(Filter, ct);
        Waiting = await structure.GetWaitingAsync(Result.Items, ct);

        if (Actor.IsSystemAdmin)
        {
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
        }
        ProjectItems = (await projects.ListOpenForPickerAsync(Filter.DepartmentId, ct: ct))
            .Select(p => new SelectListItem(Actor.IsSystemAdmin ? $"{p.Department.Name} / {p.Name}" : p.Name, p.Id.ToString(), p.Id == Filter.ProjectId)).ToList();
        var people = Actor.IsSystemAdmin
            ? await users.ListAsync(null, Filter.DepartmentId, false, ct)
            : await users.GetAssignableAsync(Actor.DepartmentId!.Value, ct);
        AssigneeItems = people.Select(u => new SelectListItem(u.DisplayName, u.Id.ToString(), u.Id == Filter.AssigneeId)).ToList();
        // The filter list may be narrowed to one department; the inline control needs candidates for every listed task.
        QuickEditAssignees = await users.GetQuickEditCandidatesAsync(ct);
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Tasks/Index", new
    {
        Filter.ProjectId, Filter.DepartmentId, Filter.Status, Filter.AssigneeId, Filter.Priority, Filter.Type, Filter.Source,
        Filter.DueBefore, Filter.DueAfter, Filter.OpenOnly, Filter.PlannedToday, Filter.PlannedFor, Filter.ParentTaskId, Filter.Unassigned, Filter.Search, Page = page
    })!;
}

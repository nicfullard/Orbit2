using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Backlog;

public class IndexModel(
    TaskService tasks,
    SprintService sprints,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    TaskStructureService structure,
    IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public TaskFilter Filter { get; set; } = new();
    /// <summary>"Reset": forget the remembered filter and show the plain backlog.</summary>
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }
    /// <summary>The page of the backlog, 100 tasks a page. Not "page", which Razor Pages keeps for its own route value (decision 46).</summary>
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    /// <summary>The filter fields remembered for the session (§6.2, §6.3).</summary>
    public static readonly string[] RememberedFilters =
    [
        nameof(TaskFilter.Search), nameof(TaskFilter.DepartmentId), nameof(TaskFilter.ProjectId), nameof(TaskFilter.Priority),
        nameof(TaskFilter.AssigneeId)
    ];
    private const string MemoryKey = "backlog";

    public Actor Actor { get; private set; } = null!;
    public PagedResult<TaskItem> Result { get; private set; } = null!;
    public IReadOnlyList<Sprint> OpenSprints { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> AssigneeItems { get; private set; } = [];
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
        else if (FilterMemory.Recall(Request, MemoryKey, Actor.UserId) is string remembered)
            return LocalRedirect(Request.Path + remembered);

        Filter.BacklogOnly = true;
        Filter.OpenOnly = true;
        Filter.SprintId = null;
        Filter.Page = PageNumber;
        Filter.PageSize = 100;
        Result = await tasks.ListAsync(Filter, ct);
        // Planning the last tasks off a page leaves it empty: show the last page that still has tasks instead.
        if (Result.Items.Count == 0 && Result.TotalCount > 0 && Result.Page > 1)
        {
            Filter.Page = Result.TotalPages;
            Result = await tasks.ListAsync(Filter, ct);
        }
        Waiting = await structure.GetWaitingAsync(Result.Items, ct);
        OpenSprints = await sprints.ListOpenAsync(ct);

        if (Actor.CanAnywhere(Permission.TasksView))
        {
            DepartmentItems = (await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
        }
        ProjectItems = (await projects.ListOpenForPickerAsync(Filter.DepartmentId, ct: ct))
            .Select(p => new SelectListItem(Actor.CanAnywhere(Permission.TasksView) ? $"{p.Department.Name} / {p.Name}" : p.Name, p.Id.ToString(), p.Id == Filter.ProjectId)).ToList();
        var people = Actor.CanAnywhere(Permission.TasksView)
            ? await users.ListAsync(null, Filter.DepartmentId, false, ct)
            : Actor.DepartmentId is Guid ownDept ? await users.GetAssignableAsync(ownDept, ct) : [];
        AssigneeItems = people.Select(u => new SelectListItem(u.DisplayName, u.Id.ToString(), u.Id == Filter.AssigneeId)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostPlanAsync(Guid? sprintId, Guid[]? selected, CancellationToken ct)
    {
        if (selected is null || selected.Length == 0)
        {
            Error("Select at least one task.");
        }
        else if (sprintId is null)
        {
            Error("Choose a sprint to plan the selected tasks into.");
        }
        else
        {
            try
            {
                var moved = await tasks.MoveToSprintAsync(selected, sprintId, ct);
                Success($"{moved} task(s) planned into the sprint.");
            }
            catch (ValidationException ex) { Error(ex.Message); }
        }
        return RedirectToPage(new
        {
            Filter.ProjectId, Filter.DepartmentId, Filter.Priority, Filter.AssigneeId, Filter.Search, PageNumber
        });
    }

    public string PageUrl(int page) => Url.Page("/Backlog/Index", new
    {
        Filter.Search, Filter.DepartmentId, Filter.ProjectId, Filter.Priority, Filter.AssigneeId, PageNumber = page
    })!;
}

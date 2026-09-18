using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
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

    public Actor Actor { get; private set; } = null!;
    public PagedResult<TaskItem> Result { get; private set; } = null!;
    public IReadOnlyList<Sprint> OpenSprints { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> AssigneeItems { get; private set; } = [];
    public IReadOnlyDictionary<Guid, WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, WaitingSummary>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Filter.BacklogOnly = true;
        Filter.OpenOnly = true;
        Filter.SprintId = null;
        Filter.PageSize = 100;
        Result = await tasks.ListAsync(Filter, ct);
        Waiting = await structure.GetWaitingAsync(Result.Items, ct);
        OpenSprints = await sprints.ListOpenAsync(ct);

        var showAllDepartments = Actor.IsSystemAdmin || Filter.AllDepartments;
        if (showAllDepartments)
        {
            DepartmentItems = (await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
        }
        ProjectItems = (await projects.ListOpenForPickerAsync(Filter.DepartmentId, ct: ct))
            .Select(p => new SelectListItem(Actor.IsSystemAdmin ? $"{p.Department.Name} / {p.Name}" : p.Name, p.Id.ToString(), p.Id == Filter.ProjectId)).ToList();
        var people = Actor.IsSystemAdmin
            ? await users.ListAsync(null, Filter.DepartmentId, false, ct)
            : await users.GetAssignableAsync(Actor.DepartmentId!.Value, ct);
        AssigneeItems = people.Select(u => new SelectListItem(u.DisplayName, u.Id.ToString(), u.Id == Filter.AssigneeId)).ToList();
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
            Filter.ProjectId, Filter.DepartmentId, Filter.Priority, Filter.AssigneeId, Filter.AllDepartments, Filter.Search
        });
    }
}

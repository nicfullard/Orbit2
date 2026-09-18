using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Projects;

public class DetailsModel(ProjectService projects, UserDirectoryService users, TaskStructureService structure, IActorProvider actors) : OrbitPageModel
{
    public IReadOnlyDictionary<Guid, WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, WaitingSummary>();
    [BindProperty(SupportsGet = true)] public TaskItemStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? AssigneeId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }

    public Actor Actor { get; private set; } = null!;
    public Project Project { get; private set; } = null!;
    public ProjectStatusSummary Summary { get; private set; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public IReadOnlyList<ApplicationUser> Assignees { get; private set; } = [];
    /// <summary>Candidates for the inline assignee control (a cross-department project's tasks span departments).</summary>
    public IReadOnlyList<UserSummary> QuickEditAssignees { get; private set; } = [];
    /// <summary>Departments with tasks on this project, for the filter (more than one on a cross-department project).</summary>
    public IReadOnlyList<Department> Departments { get; private set; } = [];
    public bool CanEdit { get; private set; }
    /// <summary>The project's own department (or a System Admin) may add tasks; a department that only shares it may not.</summary>
    public bool CanAddTasks { get; private set; }
    /// <summary>True when tasks or recurring definitions from a department other than the project's are filed under it (§6.2.1).</summary>
    public bool IsCrossDepartment { get; private set; }
    /// <summary>True when the viewer is here only because their department has tasks on the project.</summary>
    public bool IsSharedView { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Project = await projects.GetAsync(id, ct);
        Summary = await projects.GetStatusAsync(id, ct);
        CanEdit = AccessPolicy.CanEditProject(Actor, Project);
        CanAddTasks = Project.Status != ProjectStatus.Archived && AccessPolicy.CanAddTaskToProject(Actor, Project);
        IsSharedView = !Actor.CanAccessDepartment(Project.DepartmentId);
        IsCrossDepartment = Summary.IsCrossDepartment
            || Project.RecurringTaskDefinitions.Any(r => r.DepartmentId != Project.DepartmentId);

        foreach (var t in Project.Tasks) t.Project = Project;
        var tasks = Project.Tasks.AsEnumerable();
        if (Status is TaskItemStatus s) tasks = tasks.Where(t => t.Status == s);
        if (AssigneeId is Guid a) tasks = tasks.Where(t => t.AssigneeId == a);
        if (DepartmentId is Guid d) tasks = tasks.Where(t => t.DepartmentId == d);
        Tasks = tasks
            .OrderBy(t => t.Status.IsClosed()).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ThenByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt).ToList();
        Assignees = Project.Tasks.Where(t => t.Assignee is not null).Select(t => t.Assignee!)
            .DistinctBy(u => u.Id).OrderBy(u => u.DisplayName).ToList();
        Departments = Project.Tasks.Select(t => t.Department)
            .DistinctBy(x => x.Id).OrderBy(x => x.Name).ToList();
        QuickEditAssignees = await users.GetQuickEditCandidatesAsync(ct);
        Waiting = await structure.GetWaitingAsync(Tasks, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await projects.ArchiveAsync(id, ct);
            Success("Project archived. Its tasks are kept.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }
}

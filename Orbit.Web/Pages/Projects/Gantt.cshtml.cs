using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Projects;

/// <summary>The Gantt view of one project (spec §6.16): what §6.15 records, drawn - with drag-to-reschedule and the critical path.</summary>
public class GanttModel(ProjectService projects, TaskService tasks, TaskStructureService structure, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public bool HideClosed { get; set; }

    public Actor Actor { get; private set; } = null!;
    public Project Project { get; private set; } = null!;
    public GanttChart Chart { get; private set; } = null!;
    /// <summary>Show each row's department when tasks from more than one department are filed under the project (§6.2.1).</summary>
    public bool IsCrossDepartment { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Project = await projects.GetAsync(id, ct); // enforces who may see the project
        var links = await structure.ListForProjectAsync(id, ct);
        IsCrossDepartment = Project.Tasks.Any(t => t.DepartmentId != Project.DepartmentId);
        Chart = GanttChart.Build(Project.Tasks, links, DateOnly.FromDateTime(DateTime.UtcNow), HideClosed);
        return Page();
    }

    /// <summary>
    /// Drag-to-reschedule (§6.16): the hidden form posts the dragged task's new planned dates. Only that task moves -
    /// successors are never shifted - and the same rights, validation and audit apply as on the task form.
    /// </summary>
    public async Task<IActionResult> OnPostRescheduleAsync(Guid id, Guid taskId, DateOnly? startDate, DateOnly? dueDate, CancellationToken ct)
    {
        try
        {
            var task = await tasks.ChangeDatesAsync(taskId, startDate, dueDate, ct);
            var when = task.StartDate is DateOnly s && task.DueDate is DateOnly d ? $"{s:d MMM} to {d:d MMM}"
                : task.DueDate is DateOnly due ? $"due {due:d MMM}"
                : task.StartDate is DateOnly start ? $"starts {start:d MMM}"
                : "no dates";
            Success($"\"{Ui.Truncate(task.Title, 40)}\" rescheduled: {when}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id, HideClosed });
    }
}

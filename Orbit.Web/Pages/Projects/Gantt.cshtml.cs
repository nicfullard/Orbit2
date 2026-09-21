using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Projects;

/// <summary>
/// The Gantt view of one project (spec §6.16): what §6.15 records, drawn - with drag-to-reschedule - and the project's
/// last critical path analysis (§6.17) drawn over it, with the button that runs a new one.
/// </summary>
public class GanttModel(ProjectService projects, TaskService tasks, TaskStructureService structure, CriticalPathService criticalPaths, WorkingCalendarService calendars, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public bool HideClosed { get; set; }

    public Actor Actor { get; private set; } = null!;
    public Project Project { get; private set; } = null!;
    public GanttChart Chart { get; private set; } = null!;
    /// <summary>Show each row's department when tasks from more than one department are filed under the project (§6.2.1).</summary>
    public bool IsCrossDepartment { get; private set; }
    /// <summary>The last stored analysis, or null when none has been run yet.</summary>
    public CriticalPathView? Analysis { get; private set; }
    public bool CanRunAnalysis { get; private set; }
    public IReadOnlyDictionary<Guid, TaskItem> TasksById { get; private set; } = new Dictionary<Guid, TaskItem>();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Project = await projects.GetAsync(id, ct); // enforces who may see the project
        var links = await structure.ListForProjectAsync(id, ct);
        IsCrossDepartment = Project.Tasks.Any(t => t.DepartmentId != Project.DepartmentId);
        TasksById = Project.Tasks.ToDictionary(t => t.Id);
        CanRunAnalysis = AccessPolicy.CanRunCriticalPath(Actor, Project);
        Analysis = await criticalPaths.GetLatestAsync(id, ct);
        var overlay = Analysis is null ? null : new GanttOverlay(
            Analysis.Result.CriticalIds, Analysis.Result.NearCriticalIds, Analysis.Result.DrivingLinkIds.ToHashSet(),
            Analysis.Result.Schedule.PlannedCompletion, Analysis.Result.Schedule.TargetDate, Analysis.Result.Schedule.InternalCompletion);
        Chart = GanttChart.Build(Project.Tasks, links, DateOnly.FromDateTime(DateTime.UtcNow), HideClosed, overlay, await calendars.BuildAsync(ct));
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

    /// <summary>Run Critical Path Analysis (§6.17): a deliberate action, never automatic. A blocked run stores nothing and says why.</summary>
    public async Task<IActionResult> OnPostRunAnalysisAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var result = await criticalPaths.RunAsync(id, ct);
            if (result.Blocked)
                Error("Analysis not run. " + string.Join(" ", result.Errors.Select(e => e.Message)));
            else
                Success(Describe(result));
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id, HideClosed });
    }

    public static string Describe(CriticalPathResult r)
    {
        var s = r.Schedule;
        var buffer = s.BufferStatus == BufferStatus.NotAvailable ? "project buffer not available (no target date)"
            : s.BufferConsumptionPercent is int pct ? $"buffer {s.BufferStatus.Label().ToUpperInvariant()} ({pct}% consumed, {s.BufferRemainingDays} working day(s) remaining)"
            : $"buffer {s.BufferStatus.Label().ToUpperInvariant()}";
        return $"Critical path analysis complete: {r.CriticalTaskCount} critical task(s) on {r.CriticalPaths.Count} path(s), " +
               $"planned completion {s.PlannedCompletion:d MMM yyyy}, {buffer}, {r.WarningCount} warning(s).";
    }
}

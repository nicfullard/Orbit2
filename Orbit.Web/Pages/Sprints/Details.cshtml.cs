using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Sprints;

public class DetailsModel(SprintService sprints, TaskService tasks, UserDirectoryService users, IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public Sprint Sprint { get; private set; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    /// <summary>Candidates for the inline assignee control (a sprint spans departments).</summary>
    public IReadOnlyList<UserSummary> QuickEditAssignees { get; private set; } = [];
    public IReadOnlyList<NameCountRow> ByDepartment { get; private set; } = [];
    public int Done => Tasks.Count(t => t.Status == TaskItemStatus.Done);
    public int Percent => Tasks.Count == 0 ? 0 : (int)Math.Round(Done * 100.0 / Tasks.Count);

    public sealed record NameCountRow(string Name, int Total, int Open);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Sprint = await sprints.GetAsync(id, ct);
        foreach (var t in Sprint.Tasks) t.Sprint = Sprint;
        Tasks = Sprint.Tasks
            .OrderBy(t => t.Status.IsClosed()).ThenBy(t => t.Department.Name).ThenByDescending(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
            .ToList();
        ByDepartment = Tasks.GroupBy(t => t.Department.Name).OrderBy(g => g.Key)
            .Select(g => new NameCountRow(g.Key, g.Count(), g.Count(t => t.IsOpen))).ToList();
        QuickEditAssignees = await users.GetQuickEditCandidatesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, Guid[]? selected, CancellationToken ct)
    {
        if (selected is null || selected.Length == 0)
        {
            Error("Select at least one task.");
            return RedirectToPage(new { id });
        }
        try
        {
            var moved = await tasks.MoveToSprintAsync(selected, null, ct);
            Success($"{moved} task(s) moved back to the backlog.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var s = await sprints.StartAsync(id, ct);
            Success($"Sprint \"{s.Name}\" is now active.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var s = await sprints.CompleteAsync(id, ct);
            Success($"Sprint \"{s.Name}\" completed. Open tasks returned to the backlog.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Today;

/// <summary>The day plan (spec §6.12): what the team picked to work on today, grouped by person.</summary>
public class IndexModel(TaskService tasks, UserDirectoryService users, DepartmentService departments, IActorProvider actors) : OrbitPageModel
{
    /// <summary>System Admins may narrow the plan to one department; ignored for everyone else.</summary>
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }

    public Actor Actor { get; private set; } = null!;
    public DateOnly Today { get; private set; }
    public DayPlan Plan { get; private set; } = null!;
    public IReadOnlyList<AssigneeGroup> Groups { get; private set; } = [];
    public IReadOnlyList<UserSummary> QuickEditAssignees { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];

    public sealed record AssigneeGroup(Guid? AssigneeId, string Name, IReadOnlyList<TaskItem> Tasks)
    {
        public int Done => Tasks.Count(t => t.Status == TaskItemStatus.Done);
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!Actor.IsSystemAdmin) DepartmentId = null;
        Plan = await tasks.GetDayPlanAsync(Today, DepartmentId, ct);
        Groups = Plan.Planned
            .GroupBy(t => t.AssigneeId)
            .Select(g => new AssigneeGroup(g.Key, g.First().Assignee?.DisplayName ?? "Unassigned", g.ToList()))
            .OrderBy(g => g.AssigneeId is null).ThenBy(g => g.Name)
            .ToList();
        QuickEditAssignees = await users.GetQuickEditCandidatesAsync(ct);
        if (Actor.IsSystemAdmin)
        {
            DepartmentItems = (await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
        }
    }

    public async Task<IActionResult> OnPostCarryOverAsync(Guid[]? selected, CancellationToken ct)
    {
        if (selected is null || selected.Length == 0)
        {
            Error("Select at least one task.");
            return RedirectToPage(new { DepartmentId });
        }
        return await CarryOverAsync(selected, ct);
    }

    /// <summary>Carries over everything still unfinished from the previous plan; the list is recomputed server-side.</summary>
    public async Task<IActionResult> OnPostCarryOverAllAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        var plan = await tasks.GetDayPlanAsync(DateOnly.FromDateTime(DateTime.UtcNow), actor.IsSystemAdmin ? DepartmentId : null, ct);
        return await CarryOverAsync(plan.Unfinished.Select(t => t.Id).ToList(), ct);
    }

    private async Task<IActionResult> CarryOverAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        try
        {
            var moved = await tasks.CarryOverAsync(ids, DateOnly.FromDateTime(DateTime.UtcNow), ct);
            Success(moved == 0 ? "Nothing to carry over." : $"{moved} task(s) carried over to today.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { DepartmentId });
    }
}

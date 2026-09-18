using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Tasks;

public class DetailsModel(
    TaskService tasks,
    CommentService comments,
    TimeEntryService time,
    AuditService audit,
    UserDirectoryService users,
    TaskStructureService structure,
    IActorProvider actors) : OrbitPageModel
{
    public sealed class TimeForm
    {
        [DataType(DataType.Date)] public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
        [Range(1, 1440)] public int DurationMinutes { get; set; } = 60;
        [StringLength(1000)] public string? Note { get; set; }
        public Guid? UserId { get; set; }
    }

    public Actor Actor { get; private set; } = null!;
    public TaskItem Task { get; private set; } = null!;
    public IReadOnlyList<Comment> Comments { get; private set; } = [];
    public IReadOnlyList<TimeEntry> TimeEntries { get; private set; } = [];
    public IReadOnlyList<AuditLog> Activity { get; private set; } = [];
    public IReadOnlyList<UserSummary> TimeUsers { get; private set; } = [];
    public int TotalMinutes => TimeEntries.Sum(e => e.DurationMinutes);
    public bool CanEdit { get; private set; }
    public bool CanLogTime { get; private set; }
    public bool CanLogForOthers { get; private set; }
    /// <summary>The current user's clock, when it is running on this task.</summary>
    public RunningClock? Clock { get; private set; }
    public bool CanStartClock { get; private set; }
    public IReadOnlyList<TaskItemStatus> Statuses { get; private set; } = [];
    /// <summary>Subtasks and dependencies (§6.15).</summary>
    public TaskStructure Structure { get; private set; } = null!;
    /// <summary>Tasks this one can be linked to: the same project, or the same department's standalone tasks.</summary>
    public IReadOnlyList<TaskItem> LinkCandidates { get; private set; } = [];
    /// <summary>Whether the actor may add a link from this task's page (edit rights on it; the far end is checked server-side).</summary>
    public bool CanLink { get; private set; }
    public bool CanAddSubtask { get; private set; }

    [BindProperty] public string? CommentBody { get; set; }
    [BindProperty] public TimeForm Time { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCommentAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await comments.AddAsync(id, CommentBody ?? string.Empty, ct);
            Success("Comment added.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTimeAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await time.AddAsync(new TimeEntryInput
            {
                TaskId = id, UserId = Time.UserId, Date = Time.Date, DurationMinutes = Time.DurationMinutes, Note = Time.Note
            }, ct);
            Success($"Logged {TimeFormat.Minutes(Time.DurationMinutes)}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStartClockAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var started = await time.StartClockAsync(id, ct);
            Success(started.Previous is null ? "Clock started." : $"Clock started. {StopMessage(started.Previous, includeTask: true)}");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStopClockAsync(Guid id, CancellationToken ct)
    {
        var stopped = await time.StopClockAsync(id, ct);
        if (stopped is null) Error("No clock is running on this task.");
        else Success(StopMessage(stopped, includeTask: false));
        return RedirectToPage(new { id });
    }

    /// <summary>Called by <c>navigator.sendBeacon</c> when the user leaves the page while the clock is running.</summary>
    public async Task<IActionResult> OnPostStopClockBeaconAsync(Guid id, CancellationToken ct)
    {
        await time.StopClockAsync(id, ct);
        return new NoContentResult();
    }

    private static string StopMessage(ClockStopResult stopped, bool includeTask)
    {
        var on = includeTask ? $" on \"{Ui.Truncate(stopped.Task.Title, 60)}\"" : string.Empty;
        return stopped.Entry is null
            ? $"Stopped the clock{on} after less than a minute; nothing was logged."
            : $"Stopped the clock{on} and logged {TimeFormat.Minutes(stopped.Minutes)}.";
    }

    public async Task<IActionResult> OnPostDeleteTimeAsync(Guid id, Guid entryId, CancellationToken ct)
    {
        try
        {
            await time.DeleteAsync(entryId, ct);
            Success("Time entry deleted.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    /// <summary>Add a dependency (§6.15). direction "waits-on": this task waits on the other; "blocks": the other waits on this task.</summary>
    public async Task<IActionResult> OnPostAddDependencyAsync(Guid id, Guid? otherTaskId, string? direction, DependencyType type, int lagDays, CancellationToken ct)
    {
        if (otherTaskId is not Guid other)
        {
            Error("Choose a task to link.");
            return RedirectToPage(new { id });
        }
        try
        {
            var waitsOn = direction != "blocks";
            var link = await structure.AddAsync(new DependencyInput
            {
                PredecessorTaskId = waitsOn ? other : id,
                SuccessorTaskId = waitsOn ? id : other,
                Type = type,
                LagDays = lagDays
            }, ct);
            Success($"\"{Ui.Truncate(link.Successor.Title, 40)}\" now waits on \"{Ui.Truncate(link.Predecessor.Title, 40)}\" ({link.Type.Code()}).");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveDependencyAsync(Guid id, Guid linkId, CancellationToken ct)
    {
        try
        {
            await structure.RemoveAsync(linkId, ct);
            Success("Dependency removed.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Task = await tasks.GetAsync(id, ct);

        // A clock still running on a different task means the browser never sent the page-leave beacon
        // (crash, killed tab, ...). Opening another task counts as having left it, so stop and log it now.
        var clock = await time.GetRunningClockAsync(ct);
        if (clock is not null && clock.TaskId != id)
        {
            var stopped = await time.StopClockAsync(clock.TaskId, ct);
            if (stopped is not null) Success(StopMessage(stopped, includeTask: true));
        }
        else
        {
            Clock = clock;
        }
        CanStartClock = Actor.UserId is Guid clockUser && AccessPolicy.CanLogTimeFor(Actor, Task, clockUser);

        Comments = await comments.ListAsync(id, ct);
        TimeEntries = await time.ListForTaskAsync(id, ct);
        Activity = (await audit.ListAsync(new AuditFilter { EntityId = id, From = Task.CreatedAt.AddSeconds(-1), To = DateTime.UtcNow.AddMinutes(1), PageSize = 30 }, ct)).Items;
        CanEdit = AccessPolicy.CanEditTask(Actor, Task);
        CanLogForOthers = Actor.IsAdminFor(Task.DepartmentId);
        CanLogTime = Actor.UserId is Guid me && AccessPolicy.CanLogTimeFor(Actor, Task, me) || CanLogForOthers;
        Statuses = Ui.AllowedStatuses(Actor, Task);
        if (CanLogForOthers) TimeUsers = await users.GetAssignableAsync(Task.DepartmentId, ct);

        Structure = await structure.GetStructureAsync(Task, ct);
        CanLink = CanEdit;
        CanAddSubtask = Task.IsOpen && (Task.Project is null
            ? Actor.CanAccessDepartment(Task.DepartmentId)
            : Task.Project.Status != ProjectStatus.Archived && AccessPolicy.CanAddTaskToProject(Actor, Task.Project));
        if (CanLink) LinkCandidates = await structure.ListLinkCandidatesAsync(Task, ct);
    }
}

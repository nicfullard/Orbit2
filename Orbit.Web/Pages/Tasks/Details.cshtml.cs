using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
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
    AttachmentService attachments,
    RequestService requests,
    NagiosService nagios,
    IOptions<AttachmentOptions> attachmentOptions,
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
    /// <summary>An open, unassigned task within the actor's tasks.take reach: they may take it for themselves (§6.5).</summary>
    public bool CanTake { get; private set; }
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
    /// <summary>Files attached to the task (§6.18).</summary>
    public IReadOnlyList<Attachment> Attachments { get; private set; } = [];
    public bool CanAttach { get; private set; }
    public AttachmentOptions AttachmentLimits => attachmentOptions.Value;
    /// <summary>The request whose task step created this task (§6.20), for the link in the badges.</summary>
    public (Guid Id, string Number)? FromRequest { get; private set; }
    /// <summary>The Nagios problem this task was raised for (§6.21): what it is and whether Nagios still reports it.</summary>
    public NagiosTaskLink? FromNagios { get; private set; }

    /// <summary>An upload may exceed the default request body limit; raise it before the files are read (see Uploads).</summary>
    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        if (context.HandlerMethod?.MethodInfo.Name == nameof(OnPostAttachAsync)) Uploads.AllowUploadBody(HttpContext, AttachmentLimits);
    }

    public async Task<IActionResult> OnPostAttachAsync(Guid id, List<IFormFile> files, CancellationToken ct)
    {
        await AttachAsync(id, files, ct);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        try
        {
            await attachments.DeleteAsync(attachmentId, ct);
            Success("Attachment deleted.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    private async Task AttachAsync(Guid id, List<IFormFile> files, CancellationToken ct)
    {
        if (files.Count == 0) { Error("Choose at least one file."); return; }
        if (files.Count > AttachmentLimits.MaxFilesPerUpload) { Error($"At most {AttachmentLimits.MaxFilesPerUpload} files per upload."); return; }
        var added = new List<string>();
        foreach (var file in files)
        {
            try
            {
                await using var content = file.OpenReadStream();
                var a = await attachments.AddToTaskAsync(id, new AttachmentUpload(file.FileName, file.ContentType, file.Length, content), ct);
                added.Add(a.FileName);
            }
            catch (ValidationException ex) { Error(ex.Message); }
        }
        if (added.Count > 0) Success(added.Count == 1 ? $"Attached \"{added[0]}\"." : $"Attached {added.Count} files.");
    }

    [BindProperty] public string? CommentBody { get; set; }
    [BindProperty] public TimeForm Time { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        if (Task.Source == TaskSource.Request) FromRequest = await requests.ForTaskAsync(id, ct);
        if (Task.Source == TaskSource.Nagios) FromNagios = await nagios.ForTaskAsync(id, ct);
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
            if (started.AlreadyRunning) Success("The clock is already running on this task.");
            else Success(started.Previous is null ? "Clock started." : $"{StopMessage(started.Previous, includeTask: false)} Clock started again.");
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

    /// <summary>
    /// The clock script's heartbeat, every minute while the clock runs (§6.10): keeps the clock alive, and answers
    /// whether it still runs on this task. When it doesn't (stopped from another tab of this task, or found stale after
    /// the computer slept), the page reloads and shows the flash set here.
    /// </summary>
    public async Task<IActionResult> OnPostClockHeartbeatAsync(Guid id, CancellationToken ct)
    {
        var beat = await time.HeartbeatAsync(id, ct);
        if (beat.Stopped is not null) Success(StopMessage(beat.Stopped, includeTask: false));
        return new JsonResult(new { running = beat.Running });
    }

    private static string StopMessage(ClockStopResult stopped, bool includeTask)
    {
        var on = includeTask ? $" on \"{Ui.Truncate(stopped.Task.Title, 60)}\"" : string.Empty;
        // A stale clock: its page stopped checking in, so its time ends there.
        var at = stopped.PageLostAt is DateTime lost ? $" at {Ui.When(lost)}, when its page stopped responding," : string.Empty;
        return stopped.Entry is null
            ? $"Stopped the clock{on}{at} after less than a minute; nothing was logged."
            : $"Stopped the clock{on}{at} and logged {TimeFormat.Minutes(stopped.Minutes)}.";
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

    /// <summary>Assign the task to one more person (§6.2.3), without saving anything else on it.</summary>
    public async Task<IActionResult> OnPostAssignAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        if (userId is not Guid person)
        {
            Error("Choose someone to assign the task to.");
            return RedirectToPage(new { id });
        }
        try
        {
            var task = await tasks.AddAssigneeAsync(id, person, ct);
            Success($"Assigned to {task.Assignees.First(u => u.Id == person).DisplayName}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid id, Guid userId, CancellationToken ct)
    {
        try
        {
            var actor = await actors.GetAsync(ct);
            var task = await tasks.RemoveAssigneeAsync(id, userId, ct);
            Success(userId == actor.UserId ? "You are no longer assigned to this task." : "Assignee removed.");
            // Someone whose only tie to the task was being assigned to it may not be able to open it any more.
            if (!AccessPolicy.CanViewTask(actor, task)) return RedirectToPage("/Tasks/My");
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

        // Clocks on other tasks run on in their own tabs (§6.10). One whose page has stopped checking in (crash,
        // killed tab, ...) is stopped at its last heartbeat; the sweep job would get it within a minute anyway, but
        // stopping it here lets this page say so.
        var stale = await time.StopStaleClocksAsync(ct);
        if (stale.Count > 0) Success(string.Join(" ", stale.Select(s => StopMessage(s, includeTask: true))));
        Clock = await time.GetRunningClockAsync(id, ct);
        CanStartClock = Actor.UserId is Guid clockUser && AccessPolicy.CanLogTimeFor(Actor, Task, clockUser);

        Comments = await comments.ListAsync(id, ct);
        TimeEntries = await time.ListForTaskAsync(id, ct);
        Activity = (await audit.ListAsync(new AuditFilter { EntityId = id, From = Task.CreatedAt.AddSeconds(-1), To = DateTime.UtcNow.AddMinutes(1), PageSize = 30 }, ct)).Items;
        CanEdit = AccessPolicy.CanEditTask(Actor, Task);
        CanTake = AccessPolicy.CanTakeTask(Actor, Task);
        CanLogForOthers = AccessPolicy.CanLogTimeForOthers(Actor, Task);
        CanLogTime = Actor.UserId is Guid me && AccessPolicy.CanLogTimeFor(Actor, Task, me) || CanLogForOthers;
        Statuses = Ui.AllowedStatuses(Actor, Task);
        if (CanLogForOthers) TimeUsers = await users.GetAssignableAsync(Task.DepartmentId, ct);

        Structure = await structure.GetStructureAsync(Task, ct);
        CanLink = CanEdit && Task.ProjectId is not null; // dependencies need a project (§6.15)
        CanAddSubtask = Task.IsOpen && (Task.Project is null
            ? AccessPolicy.CanCreateTaskIn(Actor, Task.DepartmentId)
            : Task.Project.Status != ProjectStatus.Archived && AccessPolicy.CanAddTaskToProject(Actor, Task.Project));
        if (CanLink) LinkCandidates = await structure.ListLinkCandidatesAsync(Task, ct);

        Attachments = await attachments.ListForTaskAsync(id, ct);
        CanAttach = AccessPolicy.CanAttachToTask(Actor, Task);
    }
}

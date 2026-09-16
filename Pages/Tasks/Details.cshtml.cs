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
    public IReadOnlyList<TaskItemStatus> Statuses { get; private set; } = [];

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

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Task = await tasks.GetAsync(id, ct);
        Comments = await comments.ListAsync(id, ct);
        TimeEntries = await time.ListForTaskAsync(id, ct);
        Activity = (await audit.ListAsync(new AuditFilter { EntityId = id, From = Task.CreatedAt.AddSeconds(-1), To = DateTime.UtcNow.AddMinutes(1), PageSize = 30 }, ct)).Items;
        CanEdit = AccessPolicy.CanEditTask(Actor, Task);
        CanLogForOthers = Actor.IsAdminFor(Task.DepartmentId);
        CanLogTime = Actor.UserId is Guid me && AccessPolicy.CanLogTimeFor(Actor, Task, me) || CanLogForOthers;
        Statuses = Ui.AllowedStatuses(Actor, Task);
        if (CanLogForOthers) TimeUsers = await users.GetAssignableAsync(Task.DepartmentId, ct);
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Time;

public class EditModel(TimeEntryService time, IActorProvider actors) : OrbitPageModel
{
    public sealed class EntryForm
    {
        [DataType(DataType.Date)] public DateOnly Date { get; set; }
        [Range(1, 1440)] public int DurationMinutes { get; set; }
        [StringLength(1000)] public string? Note { get; set; }
    }

    [BindProperty] public EntryForm Form { get; set; } = new();
    public TimeEntry Entry { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Entry = await time.GetAsync(id, ct);
        AccessPolicy.Require(AccessPolicy.CanEditTimeEntry(actor, Entry, Entry.Task), "You can only edit your own time entries.");
        Form = new EntryForm { Date = Entry.Date, DurationMinutes = Entry.DurationMinutes, Note = Entry.Note };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Entry = await time.GetAsync(id, ct);
        if (ModelState.IsValid)
        {
            try
            {
                await time.UpdateAsync(id, new TimeEntryInput { TaskId = Entry.TaskId, Date = Form.Date, DurationMinutes = Form.DurationMinutes, Note = Form.Note }, ct);
                Success("Time entry saved.");
                return RedirectToPage("/Tasks/Details", new { id = Entry.TaskId });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var taskId = await time.DeleteAsync(id, ct);
            Success("Time entry deleted.");
            return RedirectToPage("/Tasks/Details", new { id = taskId });
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }
}

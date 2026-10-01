using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

/// <summary>Endpoint behind the inline status control: the POST that changes the status, and the check site.js makes before Done.</summary>
public class StatusModel(TaskService tasks, TimeEntryService time) : OrbitPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tasks/Index");

    /// <summary>Asked by site.js before a status control sets the task Done (§6.10): the confirmation to show, or null.</summary>
    public async Task<IActionResult> OnGetDoneCheckAsync(Guid id, CancellationToken ct) =>
        new JsonResult(new
        {
            warning = await time.AskBeforeDoneWithoutTimeAsync(id, ct)
                ? "You haven't logged any time on this task. Mark it Done anyway?"
                : null
        });

    public async Task<IActionResult> OnPostAsync(Guid id, TaskItemStatus status, string? returnUrl, CancellationToken ct)
    {
        try
        {
            var task = await tasks.ChangeStatusAsync(id, status, ct);
            Success($"\"{Ui.Truncate(task.Title, 40)}\" is now {status.Label()}.");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
        }
        return LocalRedirect(SafeReturnUrl(returnUrl, "/Tasks"));
    }
}

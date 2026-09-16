using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

/// <summary>POST-only endpoints behind the inline assignee and due-date controls in task lists (see Status for status).</summary>
public class QuickModel(TaskService tasks) : OrbitPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tasks/Index");

    public async Task<IActionResult> OnPostAssigneeAsync(Guid id, Guid? assigneeId, string? returnUrl, CancellationToken ct)
    {
        try
        {
            var task = await tasks.ChangeAssigneeAsync(id, assigneeId, ct);
            Success(task.Assignee is null
                ? $"\"{Ui.Truncate(task.Title, 40)}\" is now unassigned."
                : $"\"{Ui.Truncate(task.Title, 40)}\" assigned to {task.Assignee.DisplayName}.");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
        }
        return LocalRedirect(SafeReturnUrl(returnUrl, "/Tasks"));
    }

    public async Task<IActionResult> OnPostDueDateAsync(Guid id, DateOnly? dueDate, string? returnUrl, CancellationToken ct)
    {
        try
        {
            var task = await tasks.ChangeDueDateAsync(id, dueDate, ct);
            Success(task.DueDate is null
                ? $"\"{Ui.Truncate(task.Title, 40)}\" no longer has a due date."
                : $"\"{Ui.Truncate(task.Title, 40)}\" is now due {Ui.Day(task.DueDate)}.");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
        }
        return LocalRedirect(SafeReturnUrl(returnUrl, "/Tasks"));
    }
}

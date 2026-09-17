using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

/// <summary>POST-only endpoint behind the inline status control.</summary>
public class StatusModel(TaskService tasks) : OrbitPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tasks/Index");

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

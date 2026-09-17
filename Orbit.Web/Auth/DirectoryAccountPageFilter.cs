using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>
/// Keeps directory (LDAP) users off Identity's change/set-password pages: their password lives in the company
/// directory, and a password set here would do nothing. This is a courtesy, not the safeguard - the safeguard is
/// that <see cref="OrbitSignInManager"/> never consults a local password for a directory user.
/// </summary>
public sealed class DirectoryAccountPageFilter : IAsyncPageFilter
{
    private static readonly HashSet<string> PasswordPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Manage/ChangePassword",
        "/Account/Manage/SetPassword"
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var page = context.ActionDescriptor;
        var isDirectoryUser = context.HttpContext.User.FindFirstValue(OrbitClaims.AuthSource) == nameof(AuthSource.Ldap);
        if (isDirectoryUser && page.AreaName == "Identity" && PasswordPages.Contains(page.ViewEnginePath))
        {
            var tempData = context.HttpContext.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
            tempData["Error"] = "Your password is managed by your organisation's directory. Change it there, not in Orbit.";
            context.Result = new RedirectToPageResult("/Account/Manage/Index", new { area = "Identity" });
            return;
        }
        await next();
    }
}

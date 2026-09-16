using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Orbit.Application;

namespace Orbit.Auth;

/// <summary>
/// Maps domain exceptions thrown by services to sensible page results:
/// not found -> 404, forbidden on GET -> access denied page, anything else -> flash message and redirect back.
/// </summary>
public sealed class OrbitExceptionPageFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Exception is not OrbitException ex || executed.ExceptionHandled) return;

        var http = executed.HttpContext;
        if (ex is NotFoundException)
        {
            executed.Result = new NotFoundResult();
            executed.ExceptionHandled = true;
            return;
        }

        var tempDataFactory = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>();
        var tempData = tempDataFactory.GetTempData(http);
        tempData["Error"] = ex.Message;

        if (ex is ForbiddenException && HttpMethods.IsGet(http.Request.Method))
        {
            executed.Result = new ForbidResult();
        }
        else
        {
            var referer = http.Request.Headers.Referer.ToString();
            var target = !string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var uri)
                && uri.Host == http.Request.Host.Host
                ? uri.PathAndQuery
                : http.Request.Path + http.Request.QueryString;
            executed.Result = new LocalRedirectResult(target);
        }
        executed.ExceptionHandled = true;
    }
}

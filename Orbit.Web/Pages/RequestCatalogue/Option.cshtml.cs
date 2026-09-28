using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>One request option's fields and, for a flow, its questions (spec §6.20).</summary>
public class OptionModel(RequestCatalogueService catalogue, IActorProvider actors) : OrbitPageModel
{
    public RequestOption Option { get; private set; } = null!;
    public IReadOnlyList<RequestQuestion> Questions { get; private set; } = [];
    public OptionForm Form { get; private set; } = new();
    public string? SaveError { get; private set; }
    public QuestionForm NewQuestion { get; private set; } = new() { IsRequired = true };
    public string? AddError { get; private set; }
    /// <summary>Whether the link to the flow on the Requests page (requests.submit) would open.</summary>
    public bool CanSubmit { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        CanSubmit = AccessPolicy.CanSubmitRequests(await actors.GetAsync(HttpContext.RequestAborted));
        await next();
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = OptionForm.From(Option);
    }

    public async Task<IActionResult> OnPostAsync(Guid id, OptionForm form, CancellationToken ct)
    {
        try
        {
            var saved = await catalogue.UpdateOptionAsync(id, form.ToInput(), ct);
            Success($"Option \"{saved.Title}\" saved.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await LoadAsync(id, ct);
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken ct)
    {
        await catalogue.SetOptionArchivedAsync(id, archived, ct);
        Success(archived ? "Option archived: no longer offered. Nothing is deleted." : "Option restored.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var option = await catalogue.GetOptionAsync(id, ct);
        var categoryId = await catalogue.DeleteOptionAsync(id, ct);
        Success($"Option \"{option.Title}\" deleted.");
        return RedirectToPage("/RequestCatalogue/Category", new { id = categoryId });
    }

    public async Task<IActionResult> OnPostAddQuestionAsync(Guid id, QuestionForm question, CancellationToken ct)
    {
        try
        {
            await catalogue.AddQuestionAsync(id, question.ToInput(), ct);
            Success("Question added.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { AddError = ex.Message; }
        await LoadAsync(id, ct);
        Form = OptionForm.From(Option);
        NewQuestion = question;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateQuestionAsync(Guid id, Guid questionId, QuestionForm question, CancellationToken ct)
    {
        try
        {
            await catalogue.UpdateQuestionAsync(id, questionId, question.ToInput(), ct);
            Success("Question saved.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(null, null, new { id }, $"question-{questionId:N}");
    }

    public async Task<IActionResult> OnPostDeleteQuestionAsync(Guid id, Guid questionId, CancellationToken ct)
    {
        await catalogue.DeleteQuestionAsync(id, questionId, ct);
        Success("Question deleted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMoveQuestionAsync(Guid id, Guid questionId, int direction, CancellationToken ct)
    {
        await catalogue.MoveQuestionAsync(id, questionId, direction, ct);
        return RedirectToPage(null, null, new { id }, $"question-{questionId:N}");
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Option = await catalogue.GetOptionAsync(id, ct);
        Questions = Option.Questions.OrderBy(q => q.DisplayOrder).ToList();
    }
}

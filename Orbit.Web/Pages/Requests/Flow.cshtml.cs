using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Requests;

/// <summary>
/// Walking through a request flow (spec §6.20): one question per step (requests.js), an optional step for files, a review, then the
/// request is logged as a task in the flow's department. Refused answers come back beside their questions, the answers kept.
/// </summary>
public class FlowModel(
    RequestService requests,
    RequestCatalogueService catalogue,
    AssetService assets,
    IActorProvider actors,
    IOptions<AttachmentOptions> attachmentOptions) : OrbitPageModel
{
    /// <summary>What the form posts for one question: its text or choice, and for an Asset question the asset picked.</summary>
    public sealed class AnswerForm
    {
        public string? Value { get; set; }
        public Guid? AssetId { get; set; }
    }

    public RequestOption Option { get; private set; } = null!;
    public bool CanConfigure { get; private set; }
    /// <summary>
    /// The choices of the flow's Asset question: the assets the person holds, can see and haven't been disposed of. With none, the
    /// question takes a description only.
    /// </summary>
    public IReadOnlyList<AssetPickerItem> HeldAssets { get; private set; } = [];
    /// <summary>
    /// Whether a User question is asked: requests.submit above Own. At Own it is skipped - the server answers it with the person
    /// logging the request - and the review shows <see cref="MyName"/>.
    /// </summary>
    public bool AsksWhoFor { get; private set; }
    /// <summary>The User question's choices: whom the person may log for, themselves included.</summary>
    public IReadOnlyList<RequestPerson> People { get; private set; } = [];
    public string MyName { get; private set; } = string.Empty;
    public Guid? MyId { get; private set; }
    public AttachmentOptions AttachmentLimits => attachmentOptions.Value;

    [BindProperty] public Dictionary<Guid, AnswerForm> Answers { get; set; } = new();
    /// <summary>Set once when the page is first shown, so a double click or a resubmitted page logs the request once.</summary>
    [BindProperty] public string? IdempotencyKey { get; set; }

    public IReadOnlyDictionary<Guid, string> Errors { get; private set; } = new Dictionary<Guid, string>();
    public string? GeneralError { get; private set; }
    /// <summary>After a refused post that carried files: the browser can't keep them, so the page asks for them again.</summary>
    public bool FilesDropped { get; private set; }

    /// <summary>A request may carry files; raise the request body limit before they are read (see Uploads).</summary>
    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        if (context.HandlerMethod?.MethodInfo.Name == nameof(OnPostAsync)) Uploads.AllowUploadBody(HttpContext, AttachmentLimits);
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        IdempotencyKey = Guid.NewGuid().ToString("N");
    }

    public async Task<IActionResult> OnPostAsync(Guid id, List<IFormFile> files, CancellationToken ct)
    {
        var answers = Answers.ToDictionary(a => a.Key, a => new RequestAnswerInput(a.Value.Value, a.Value.AssetId));
        var uploads = files.Select(f => new AttachmentUpload(f.FileName, f.ContentType, f.Length, f.OpenReadStream())).ToList();
        try
        {
            var task = await requests.SubmitAsync(id, answers, uploads, IdempotencyKey, ct);
            return RedirectToPage("/Requests/Logged", new { id = task.Id });
        }
        catch (RequestAnswersException ex)
        {
            Errors = ex.Errors;
        }
        catch (ValidationException ex)
        {
            GeneralError = ex.Message;
        }
        finally
        {
            foreach (var u in uploads) await u.Content.DisposeAsync();
        }
        await LoadAsync(id, ct);
        FilesDropped = files.Count > 0;
        return Page();
    }

    public AnswerForm AnswerFor(Guid questionId) => Answers.GetValueOrDefault(questionId) ?? new AnswerForm();

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        Option = await requests.FlowAsync(id, ct);
        CanConfigure = await catalogue.CanConfigureAsync(Option.Category.DepartmentId, ct);
        if (Option.Questions.Any(q => q.QuestionType == RequestQuestionType.Asset))
            HeldAssets = await assets.ListHeldAsync(ct: ct);
        MyName = actor.DisplayName;
        MyId = actor.UserId;
        AsksWhoFor = AccessPolicy.CanRequestForOthers(actor);
        if (AsksWhoFor && Option.Questions.Any(q => q.QuestionType == RequestQuestionType.User))
            People = await requests.PeopleAsync(ct);
    }
}

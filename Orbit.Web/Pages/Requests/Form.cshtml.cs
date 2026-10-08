using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Requests;

/// <summary>A later form step of a request (spec §6.20), filled in by the person it is addressed to - the same wizard as logging a request.</summary>
public class FormModel(RequestService requests, IOptions<AttachmentOptions> attachmentOptions) : OrbitPageModel
{
    public RequestStep Step { get; private set; } = null!;
    public AttachmentOptions AttachmentLimits => attachmentOptions.Value;

    [BindProperty] public Dictionary<Guid, AnswerForm> Answers { get; set; } = new();

    public IReadOnlyDictionary<Guid, string> Errors { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> Labels { get; private set; } = new Dictionary<Guid, string>();
    /// <summary>What each Asset type field offers, for its list.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>> AssetTypes { get; private set; } = new Dictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>>();
    public string? GeneralError { get; private set; }
    public bool FilesDropped { get; private set; }

    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        if (context.HandlerMethod?.MethodInfo.Name == nameof(OnPostAsync)) Uploads.AllowUploadBody(HttpContext, AttachmentLimits);
    }

    public async Task OnGetAsync(Guid id, CancellationToken ct) => await LoadAsync(id, ct);

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        var answers = RequestFormPosts.Answers(Answers);
        var files = RequestFormPosts.Files(Request.Form.Files, Step.FlowStep.Fields);
        try
        {
            await requests.SubmitFormAsync(id, answers, files, ct);
            Success($"\"{Step.FlowStep.Title}\" filled in.");
            return RedirectToPage("/Requests/Request", new { id = Step.RequestId });
        }
        catch (RequestAnswersException ex)
        {
            Errors = ex.Errors;
        }
        catch (ValidationException ex)
        {
            GeneralError = ex.Message;
        }
        FilesDropped = files.Values.Any(f => f.Count > 0);
        var labels = new Dictionary<Guid, string>();
        foreach (var (fieldId, answer) in Answers)
            if (answer.Id is Guid picked && await requests.PickLabelAsync(fieldId, picked, ct) is { } label) labels[fieldId] = label;
        Labels = labels;
        return Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Step = await requests.FormStepAsync(id, ct);
        AssetTypes = await requests.AssetTypeChoicesAsync(Step.FlowStep.Flow, Step.FlowStep, ct);
    }
}

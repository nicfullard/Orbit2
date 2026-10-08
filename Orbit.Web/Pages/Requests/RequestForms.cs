using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Requests;

/// <summary>What a form posts for one field (spec §6.20): its text, choice or urgency, or the asset, asset type, project or person picked.</summary>
public sealed class AnswerForm
{
    public string? Value { get; set; }
    public Guid? Id { get; set; }
}

/// <summary>The form-fields partial: the fields to ask, what was answered, what was refused, and how the pickers search.</summary>
public sealed class RequestFormFieldsVm
{
    public required IReadOnlyList<RequestFormField> Fields { get; init; }
    public required IReadOnlyDictionary<Guid, AnswerForm> Answers { get; init; }
    public required IReadOnlyDictionary<Guid, string> Errors { get; init; }
    /// <summary>The labels of the assets, projects and people picked, by field, so a refused post shows them again.</summary>
    public required IReadOnlyDictionary<Guid, string> Labels { get; init; }
    /// <summary>What each Asset type field offers, by field, for its list.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<RequestAssetTypeChoice>> AssetTypes { get; init; }
    /// <summary>The assets each Asset field lists as buttons, by field; a field that isn't here offers too many to list, and searches.</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<RequestLookupItem>> AssetChoices { get; init; }
    public required string LookupUrl { get; init; }
    /// <summary>The request step being filled in, which the pickers' searches name; null while the request is being logged.</summary>
    public Guid? RequestStepId { get; init; }
    /// <summary>Said in place of a field scoped to the requester's department when the requester has none; null when they have one.</summary>
    public string? NoDepartmentNote { get; init; }
    public required AttachmentOptions Limits { get; init; }
    public bool FilesDropped { get; init; }

    public AnswerForm AnswerFor(Guid fieldId) => Answers.GetValueOrDefault(fieldId) ?? new AnswerForm();
}

/// <summary>What the pages that fill in forms share: turning the posted form into the service's inputs.</summary>
public static class RequestFormPosts
{
    public static IReadOnlyDictionary<Guid, RequestAnswerInput> Answers(Dictionary<Guid, AnswerForm> posted) =>
        posted.ToDictionary(a => a.Key, a => new RequestAnswerInput(a.Value.Value, a.Value.Id));

    /// <summary>The files posted for each Attachment field, under <c>files-{fieldId}</c>.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentUpload>> Files(IFormFileCollection files, IEnumerable<RequestFormField> fields) =>
        fields.Where(f => f.FieldType == RequestFieldType.Attachment).ToDictionary(f => f.Id, f => (IReadOnlyList<AttachmentUpload>)files
            .GetFiles($"files-{f.Id}").Where(x => x.Length > 0)
            .Select(x => new AttachmentUpload(x.FileName, x.ContentType, x.Length, x.OpenReadStream())).ToList());
}

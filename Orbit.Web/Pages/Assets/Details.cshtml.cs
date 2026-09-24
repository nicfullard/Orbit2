using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Assets;

public class DetailsModel(
    AssetService assets,
    CommentService comments,
    AttachmentService attachments,
    AuditService audit,
    IOptions<AttachmentOptions> attachmentOptions,
    IActorProvider actors) : OrbitPageModel
{
    public Actor Actor { get; private set; } = null!;
    public Asset Asset { get; private set; } = null!;
    /// <summary>The next check due and where the asset stands against it (§6.19).</summary>
    public AssetListItem Schedule { get; private set; } = null!;
    public IReadOnlyDictionary<Guid, string> Values { get; private set; } = new Dictionary<Guid, string>();
    /// <summary>Required properties without a value - an asset saved before they became required (§6.19).</summary>
    public IReadOnlyList<string> MissingRequired { get; private set; } = [];
    /// <summary>Other assets with the same manufacturer and serial number that the viewer can see.</summary>
    public IReadOnlyList<AssetRef> Duplicates { get; private set; } = [];
    public IReadOnlyList<Comment> Comments { get; private set; } = [];
    public IReadOnlyList<Attachment> Attachments { get; private set; } = [];
    public IReadOnlyList<AuditLog> Activity { get; private set; } = [];
    public bool IsAssigned { get; private set; }
    public bool CanEdit { get; private set; }
    /// <summary>Change who holds it right here (§6.19): assets.edit, and not disposed.</summary>
    public bool CanAssign { get; private set; }
    public bool CanCheck { get; private set; }
    /// <summary>assets.check at Own only, on an asset the viewer holds: the check form is the one-click "Confirm I have it".</summary>
    public bool ConfirmOnly { get; private set; }
    public bool WarrantyExpired { get; private set; }
    public bool WarrantyExpiring { get; private set; }
    public AttachmentOptions AttachmentLimits => attachmentOptions.Value;

    /// <summary>An upload may exceed the default request body limit; raise it before the files are read (see Uploads).</summary>
    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        if (context.HandlerMethod?.MethodInfo.Name == nameof(OnPostAttachAsync)) Uploads.AllowUploadBody(HttpContext, AttachmentLimits);
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Asset = await assets.GetAsync(id, ct);
        Schedule = assets.Item(Asset);
        IsAssigned = AccessPolicy.IsAssigned(Actor, Asset);
        CanEdit = AccessPolicy.CanEditAsset(Actor, Asset);
        CanAssign = CanEdit && Asset.Status != AssetStatus.Disposed;
        CanCheck = AccessPolicy.CanCheckAsset(Actor, Asset, IsAssigned);
        ConfirmOnly = CanCheck && IsAssigned && Actor.ScopeOf(Permission.AssetsCheck) == PermissionScope.Own;
        Values = Asset.PropertyValues.ToDictionary(v => v.AssetTypePropertyId, v => v.Value);
        MissingRequired = AssetPropertyRules.MissingRequired(Asset.AssetType.Properties, Values);
        Duplicates = await assets.PossibleDuplicatesAsync(Asset, ct);
        WarrantyExpired = assets.WarrantyExpired(Asset);
        WarrantyExpiring = assets.WarrantyExpiring(Asset);
        Comments = await comments.ListForAssetAsync(id, ct);
        Attachments = await attachments.ListForAssetAsync(id, ct);
        Activity = (await audit.ListAsync(new AuditFilter
        {
            EntityId = id, From = Asset.CreatedAt.AddSeconds(-1), To = DateTime.UtcNow.AddMinutes(1), PageSize = 50
        }, ct)).Items;
        return Page();
    }

    public async Task<IActionResult> OnPostCommentAsync(Guid id, string? commentBody, CancellationToken ct)
    {
        try
        {
            await comments.AddToAssetAsync(id, commentBody ?? string.Empty, ct);
            Success("Comment added.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    /// <summary>Hand the asset to one more person, without saving anything else on the record.</summary>
    public async Task<IActionResult> OnPostAssignAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        if (userId is not Guid person)
        {
            Error("Choose someone to assign the asset to.");
            return RedirectToPage(new { id });
        }
        try
        {
            Success($"Assigned to {await assets.AssignAsync(id, person, ct)}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid id, Guid userId, CancellationToken ct)
    {
        try
        {
            var name = await assets.UnassignAsync(id, userId, ct);
            if (name is not null) Success($"{name} no longer holds this asset.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRecordCheckAsync(Guid id, DateOnly? checkDate, AssetCheckOutcome outcome, string? notes, CancellationToken ct)
    {
        try
        {
            await assets.RecordCheckAsync(id, new AssetCheckInput { CheckDate = checkDate, Outcome = outcome, Notes = notes }, ct);
            Success($"Check recorded: {outcome.Label()}.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostConfirmHeldAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await assets.ConfirmHeldAsync(id, ct);
            Success("Thanks - recorded that you have it.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveCheckAsync(Guid id, Guid checkId, CancellationToken ct)
    {
        try
        {
            await assets.RemoveCheckAsync(id, checkId, ct);
            Success("Check removed.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAttachAsync(Guid id, List<IFormFile> files, CancellationToken ct)
    {
        if (files.Count == 0) Error("Choose at least one file.");
        else if (files.Count > AttachmentLimits.MaxFilesPerUpload) Error($"At most {AttachmentLimits.MaxFilesPerUpload} files per upload.");
        else
        {
            var added = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    await using var content = file.OpenReadStream();
                    var a = await attachments.AddToAssetAsync(id, new AttachmentUpload(file.FileName, file.ContentType, file.Length, content), ct);
                    added.Add(a.FileName);
                }
                catch (ValidationException ex) { Error(ex.Message); }
            }
            if (added.Count > 0) Success(added.Count == 1 ? $"Attached \"{added[0]}\"." : $"Attached {added.Count} files.");
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        try
        {
            await attachments.DeleteAsync(attachmentId, ct);
            Success("Attachment deleted.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }
}

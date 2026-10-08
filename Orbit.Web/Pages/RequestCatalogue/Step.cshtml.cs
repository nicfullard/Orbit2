using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Pages.Assets;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.RequestCatalogue;

/// <summary>
/// One step's settings (spec §6.20): the common ones, its dependencies, and by kind its fields (a form), stages and approvers (an
/// approval), task settings, action and bindings, or web address. Fields, stages and approvers are saved one at a time, like the
/// questions were; the rest with one Save.
/// </summary>
public class StepModel(RequestCatalogueService catalogue) : OrbitPageModel
{
    public RequestFlowStep Step { get; private set; } = null!;
    public RequestFlow Flow => Step.Flow;
    /// <summary>The flow's other steps, in order, for the dependency list.</summary>
    public IReadOnlyList<RequestFlowStep> Others { get; private set; } = [];
    /// <summary>The steps this one waits for, directly or through others, as saved: their values are what templates may use.</summary>
    public IReadOnlyList<RequestFlowStep> Predecessors { get; private set; } = [];
    public IReadOnlyList<RequestToken> Tokens { get; private set; } = [];
    public IReadOnlyList<Department> Departments { get; private set; } = [];
    public IReadOnlyList<ApplicationRole> Roles { get; private set; } = [];
    public IReadOnlyList<RequestAction> Actions { get; private set; } = [];
    public int InProgress { get; private set; }
    [BindProperty] public StepForm Form { get; set; } = new();
    public string? SaveError { get; private set; }
    public FieldForm NewField { get; private set; } = new() { IsRequired = true };
    public string? FieldError { get; private set; }
    public ApproverForm NewApprover { get; private set; } = new();
    public string? ApproverError { get; private set; }
    public Guid? ApproverStageId { get; private set; }

    /// <summary>Fields of predecessor forms of one type, for the task step's selects.</summary>
    public IEnumerable<SelectListItem> FieldChoices(RequestFieldType type, Guid? selected) =>
        new[] { new SelectListItem("None", string.Empty, selected is null) }.Concat(
            Predecessors.Where(s => s.Kind == RequestStepKind.Form).SelectMany(s => s.Fields.Where(f => f.FieldType == type)
                .Select(f => new SelectListItem($"{s.Title}: {f.Prompt}", f.Id.ToString(), f.Id == selected))));

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = StepForm.From(Step);
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var saved = await catalogue.UpdateStepAsync(id, Form.ToInput(), ct);
            Success($"Step \"{saved.Title}\" saved.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await LoadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var step = await catalogue.GetStepAsync(id, ct);
        try
        {
            var flowId = await catalogue.DeleteStepAsync(id, ct);
            Success($"Step \"{step.Title}\" deleted.");
            return RedirectToPage("/RequestCatalogue/Flow", new { id = flowId });
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
            return RedirectToPage(new { id });
        }
    }

    // ---------------------------------------------------------------- fields

    public async Task<IActionResult> OnPostAddFieldAsync(Guid id, FieldForm field, CancellationToken ct)
    {
        try
        {
            await catalogue.AddFieldAsync(id, field.ToInput(), ct);
            Success("Field added.");
            return RedirectToPage(new { id });
        }
        catch (ValidationException ex) { FieldError = ex.Message; }
        await LoadAsync(id, ct);
        Form = StepForm.From(Step);
        NewField = field;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateFieldAsync(Guid id, Guid fieldId, FieldForm field, CancellationToken ct)
    {
        try
        {
            await catalogue.UpdateFieldAsync(id, fieldId, field.ToInput(), ct);
            Success("Field saved.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(null, null, new { id }, $"field-{fieldId:N}");
    }

    public async Task<IActionResult> OnPostDeleteFieldAsync(Guid id, Guid fieldId, CancellationToken ct)
    {
        try
        {
            await catalogue.DeleteFieldAsync(id, fieldId, ct);
            Success("Field deleted.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMoveFieldAsync(Guid id, Guid fieldId, int direction, CancellationToken ct)
    {
        await catalogue.MoveFieldAsync(id, fieldId, direction, ct);
        return RedirectToPage(null, null, new { id }, $"field-{fieldId:N}");
    }

    // ---------------------------------------------------------------- stages and approvers

    public async Task<IActionResult> OnPostAddStageAsync(Guid id, ApprovalRule rule, CancellationToken ct)
    {
        try
        {
            await catalogue.AddStageAsync(id, rule, ct);
            Success("Stage added. Now add its approvers.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUpdateStageAsync(Guid id, Guid stageId, ApprovalRule rule, CancellationToken ct)
    {
        try
        {
            await catalogue.UpdateStageAsync(stageId, rule, ct);
            Success("Stage saved.");
        }
        catch (ValidationException ex) { Error(ex.Message); }
        return RedirectToPage(null, null, new { id }, $"stage-{stageId:N}");
    }

    public async Task<IActionResult> OnPostDeleteStageAsync(Guid id, Guid stageId, CancellationToken ct)
    {
        await catalogue.DeleteStageAsync(stageId, ct);
        Success("Stage deleted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMoveStageAsync(Guid id, Guid stageId, int direction, CancellationToken ct)
    {
        await catalogue.MoveStageAsync(stageId, direction, ct);
        return RedirectToPage(null, null, new { id }, $"stage-{stageId:N}");
    }

    public async Task<IActionResult> OnPostAddApproverAsync(Guid id, Guid stageId, ApproverForm approver, CancellationToken ct)
    {
        try
        {
            await catalogue.AddApproverAsync(stageId, approver.ToInput(), ct);
            Success("Approver added.");
            return RedirectToPage(null, null, new { id }, $"stage-{stageId:N}");
        }
        catch (ValidationException ex) { ApproverError = ex.Message; }
        await LoadAsync(id, ct);
        Form = StepForm.From(Step);
        NewApprover = approver;
        ApproverStageId = stageId;
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveApproverAsync(Guid id, Guid approverId, CancellationToken ct)
    {
        await catalogue.RemoveApproverAsync(approverId, ct);
        Success("Approver removed.");
        return RedirectToPage(new { id });
    }

    // ---------------------------------------------------------------- helpers

    public PersonPickerVm PersonPicker(string id, string fieldName, IEnumerable<ApplicationUser> selected, bool single, string emptyText) => new()
    {
        Id = id,
        FieldName = fieldName,
        SearchUrl = Url.Page("/RequestCatalogue/People")!,
        Selected = selected.Select(u => new UserSummary(u.Id, u.DisplayName, u.Email ?? string.Empty, new RoleRef(Guid.Empty, string.Empty, false, false), false,
            u.DepartmentId, u.Department?.Name, u.IsActive, u.IsSystemAccount, u.CreatedAt, u.AuthSource)).ToList(),
        Single = single,
        EmptyText = emptyText,
        Placeholder = "Type a name or email...",
        MarkInactive = true
    };

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Step = await catalogue.GetStepAsync(id, ct);
        Others = Flow.Steps.Where(s => s.Id != id).ToList();
        var allDeps = Flow.Steps.SelectMany(s => s.Dependencies).ToList();
        var predecessorIds = RequestFlowRules.TransitivePredecessors(id, allDeps);
        Predecessors = Flow.Steps.Where(s => predecessorIds.Contains(s.Id)).ToList();
        Tokens = RequestTokenRules.Available(Predecessors);
        InProgress = await catalogue.InProgressRequestCountAsync(Flow.Id, ct);
        if (Step.Kind == RequestStepKind.Task || Step.Kind == RequestStepKind.Approval)
            Departments = await catalogue.OpenDepartmentsAsync(ct);
        if (Step.Kind == RequestStepKind.Approval)
            Roles = await catalogue.RolesForApproversAsync(ct);
        if (Step.Kind == RequestStepKind.Action)
            Actions = await catalogue.ActionsForPickerAsync(ct);
    }
}

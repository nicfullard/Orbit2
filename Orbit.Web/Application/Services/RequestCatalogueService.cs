using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Configuring request flows (spec §6.20): a department's categories, their flows, and each flow's steps - fields, approval stages
/// and approvers, dependencies, task settings, action bindings. Everything belongs to the category's department, set when the
/// category is created and never changed; requests.configure reaching that department manages it. Every change is audited on the
/// category or on the flow (step changes are recorded on their flow). A flow with requests in progress keeps its structure: steps,
/// keys, dependencies and fields can't be removed or renamed until they finish, and a flow or step a request was ever logged with
/// can't be deleted - archive it instead.
/// </summary>
public sealed class RequestCatalogueService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    // ---------------------------------------------------------------- reads

    /// <summary>The categories within the caller's requests.configure reach, by department then display order, with their flows and steps.</summary>
    public async Task<IReadOnlyList<RequestCategory>> ListAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var categories = await Scoping.RequestCategories(LoadCategory(db.RequestCategories.AsNoTracking()), actor)
            .OrderBy(c => c.Department.Name).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Title)
            .AsSplitQuery()
            .ToListAsync(ct);
        foreach (var c in categories) Link(c);
        return categories;
    }

    /// <summary>A category for its edit page, with its department, flows and their steps.</summary>
    public async Task<RequestCategory> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await LoadCategory(db.RequestCategories.AsNoTracking()).AsSplitQuery().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        return Link(category);
    }

    /// <summary>A flow for its edit page: its category and department, and its steps with everything under them, in order.</summary>
    public async Task<RequestFlow> GetFlowAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await LoadFlow(db.RequestFlows.AsNoTracking()).AsSplitQuery().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        return Link(flow);
    }

    /// <summary>A step for its edit page, with its flow loaded as <see cref="GetFlowAsync"/> loads it (the other steps feed the dependency list and the token picker).</summary>
    public async Task<RequestFlowStep> GetStepAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flowId = await db.RequestFlowSteps.AsNoTracking().Where(s => s.Id == id).Select(s => (Guid?)s.FlowId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Request step not found.");
        var flow = await LoadFlow(db.RequestFlows.AsNoTracking()).AsSplitQuery().FirstAsync(f => f.Id == flowId, ct);
        RequireConfigure(actor, flow.Category.DepartmentId);
        Link(flow);
        return flow.Steps.First(s => s.Id == id);
    }

    /// <summary>How many requests are in progress through the flow: the builder page warns, and structural changes are refused.</summary>
    public Task<int> InProgressRequestCountAsync(Guid flowId, CancellationToken ct = default) =>
        db.Requests.CountAsync(r => r.FlowId == flowId && r.Status == RequestStatus.InProgress, ct);

    /// <summary>The action library as a flow builder sees it (requests.configure at any scope): names and parameters, to bind an Action step.</summary>
    public async Task<IReadOnlyList<RequestAction>> ActionsForPickerAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.Has(Permission.RequestsConfigure), "You don't have permission to configure request flows.");
        return await db.RequestActions.AsNoTracking().Include(a => a.Parameters.OrderBy(p => p.DisplayOrder)).OrderBy(a => a.Name).ToListAsync(ct);
    }

    /// <summary>The roles an approver may name, by name (requests.configure at any scope).</summary>
    public async Task<IReadOnlyList<ApplicationRole>> RolesForApproversAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.Has(Permission.RequestsConfigure), "You don't have permission to configure request flows.");
        return await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
    }

    /// <summary>The open departments a step may name - a task's department, an approver's (requests.configure at any scope).</summary>
    public async Task<IReadOnlyList<Department>> OpenDepartmentsAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.Has(Permission.RequestsConfigure), "You don't have permission to configure request flows.");
        return await db.Departments.AsNoTracking().Where(d => !d.IsArchived).OrderBy(d => d.Name).ToListAsync(ct);
    }

    // ---------------------------------------------------------------- categories

    public async Task<RequestCategory> CreateCategoryAsync(RequestCategoryInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        RequireConfigure(actor, departmentId);
        var dept = await RequireOpenDepartmentAsync(departmentId, ct);
        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxCategoryTitleLength);
        await RequireUniqueCategoryTitleAsync(departmentId, title, null, ct);

        var now = DateTime.UtcNow;
        var last = await db.RequestCategories.Where(c => c.DepartmentId == departmentId).MaxAsync(c => (int?)c.DisplayOrder, ct) ?? 0;
        var category = new RequestCategory
        {
            DepartmentId = departmentId,
            Title = title,
            Description = RequestFlowRules.Clean(input.Description, RequestFlowRules.MaxDescriptionLength, "The description"),
            Icon = RequestFlowRules.CleanIcon(input.Icon),
            Colour = RequestFlowRules.CleanColour(input.Colour),
            DisplayOrder = last + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RequestCategories.Add(category);
        audit.Add(actor, AuditEntity.RequestCategory, category.Id, AuditAction.Created, departmentId, category.Title,
            new { category.Title, category.Description, category.Icon, category.Colour, department = dept.Name });
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task<RequestCategory> UpdateCategoryAsync(Guid id, RequestCategoryInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxCategoryTitleLength);
        if (!string.Equals(title, category.Title, StringComparison.OrdinalIgnoreCase)) await RequireUniqueCategoryTitleAsync(category.DepartmentId, title, id, ct);
        var description = RequestFlowRules.Clean(input.Description, RequestFlowRules.MaxDescriptionLength, "The description");
        var icon = RequestFlowRules.CleanIcon(input.Icon);
        var colour = RequestFlowRules.CleanColour(input.Colour);
        var changes = new ChangeSet()
            .TrackText("title", category.Title, title)
            .TrackText("description", category.Description, description)
            .Track("icon", category.Icon, icon)
            .Track("colour", category.Colour, colour);
        if (!changes.HasChanges) return category;
        category.Title = title;
        category.Description = description;
        category.Icon = icon;
        category.Colour = colour;
        category.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestCategory, category.Id, AuditAction.Updated, category.DepartmentId, category.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return category;
    }

    /// <summary>Archive: the category and every flow under it leave the Requests page; nothing is deleted, and requests in progress carry on.</summary>
    public async Task SetCategoryArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        if (category.IsArchived == archived) return;
        category.IsArchived = archived;
        category.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestCategory, category.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, category.DepartmentId, category.Title);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete a category with its flows and their steps - only while no request was ever logged through them. Returns how many flows went.</summary>
    public async Task<int> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.Include(c => c.Flows).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var logged = await db.Requests.CountAsync(r => r.Flow.CategoryId == id, ct);
        if (logged > 0)
            throw new ValidationException($"{Count(logged, "request")} {(logged == 1 ? "was" : "were")} logged through this category's flows, so it can't be deleted. Archive it instead.");
        var flows = category.Flows.Count;
        db.RequestCategories.Remove(category); // flows and steps cascade
        audit.Add(actor, AuditEntity.RequestCategory, category.Id, AuditAction.Deleted, category.DepartmentId, category.Title,
            new { category.Title, flows = category.Flows.Select(f => f.Title).ToList() });
        await db.SaveChangesAsync(ct);
        return flows;
    }

    /// <summary>Move a category one place up (-1) or down (+1) among its department's categories.</summary>
    public async Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var siblings = await db.RequestCategories.Where(c => c.DepartmentId == category.DepartmentId).ToListAsync(ct);
        if (Reorder(siblings, c => c.Id, c => c.DisplayOrder, c => c.Title, (c, n) => c.DisplayOrder = n, id, direction))
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- flows

    public async Task<RequestFlow> AddFlowAsync(Guid categoryId, RequestFlowInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.Include(c => c.Flows).FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxFlowTitleLength);
        RequireUniqueFlowTitle(category, title, null);
        var now = DateTime.UtcNow;
        var flow = new RequestFlow
        {
            CategoryId = category.Id,
            Title = title,
            Description = RequestFlowRules.Clean(input.Description, RequestFlowRules.MaxDescriptionLength, "The description"),
            DisplayOrder = (category.Flows.Count == 0 ? 0 : category.Flows.Max(f => f.DisplayOrder)) + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RequestFlows.Add(flow);
        audit.Add(actor, AuditEntity.RequestFlow, flow.Id, AuditAction.Created, category.DepartmentId, flow.Title,
            new { flow.Title, flow.Description, category = category.Title });
        await db.SaveChangesAsync(ct);
        return flow;
    }

    public async Task<RequestFlow> UpdateFlowAsync(Guid id, RequestFlowInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await db.RequestFlows.Include(f => f.Category).ThenInclude(c => c.Flows).FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxFlowTitleLength);
        RequireUniqueFlowTitle(flow.Category, title, id);
        var description = RequestFlowRules.Clean(input.Description, RequestFlowRules.MaxDescriptionLength, "The description");
        var changes = new ChangeSet().TrackText("title", flow.Title, title).TrackText("description", flow.Description, description);
        if (!changes.HasChanges) return flow;
        flow.Title = title;
        flow.Description = description;
        flow.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestFlow, flow.Id, AuditAction.Updated, flow.Category.DepartmentId, flow.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return flow;
    }

    public async Task SetFlowArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await db.RequestFlows.Include(f => f.Category).FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        if (flow.IsArchived == archived) return;
        flow.IsArchived = archived;
        flow.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestFlow, flow.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, flow.Category.DepartmentId, flow.Title);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete a flow with its steps - only while no request was ever logged through it. Returns its category's id.</summary>
    public async Task<Guid> DeleteFlowAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await db.RequestFlows.Include(f => f.Category).Include(f => f.Steps).FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        var logged = await db.Requests.CountAsync(r => r.FlowId == id, ct);
        if (logged > 0)
            throw new ValidationException($"{Count(logged, "request")} {(logged == 1 ? "was" : "were")} logged through this flow, so it can't be deleted. Archive it instead.");
        db.RequestFlows.Remove(flow);
        audit.Add(actor, AuditEntity.RequestFlow, flow.Id, AuditAction.Deleted, flow.Category.DepartmentId, flow.Title,
            new { flow.Title, steps = flow.Steps.OrderBy(s => s.DisplayOrder).Select(s => s.Title).ToList() });
        await db.SaveChangesAsync(ct);
        return flow.CategoryId;
    }

    public async Task MoveFlowAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await db.RequestFlows.AsNoTracking().Include(f => f.Category).FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        var siblings = await db.RequestFlows.Where(f => f.CategoryId == flow.CategoryId).ToListAsync(ct);
        if (Reorder(siblings, f => f.Id, f => f.DisplayOrder, f => f.Title, (f, n) => f.DisplayOrder = n, id, direction))
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- steps

    /// <summary>Add a step at the end of the flow. Its kind is fixed; a task step starts in the flow's department, for the requester.</summary>
    public async Task<RequestFlowStep> AddStepAsync(Guid flowId, RequestStepAddInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flow = await db.RequestFlows.Include(f => f.Category).Include(f => f.Steps).FirstOrDefaultAsync(f => f.Id == flowId, ct)
            ?? throw new NotFoundException("Request flow not found.");
        RequireConfigure(actor, flow.Category.DepartmentId);
        if (!Enum.IsDefined(input.Kind)) throw new ValidationException("Choose one of the step kinds offered.");
        if (flow.Steps.Count >= RequestFlowRules.MaxSteps) throw new ValidationException($"A flow can have at most {RequestFlowRules.MaxSteps} steps.");
        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxStepTitleLength);
        var key = RequestFlowRules.CleanKey(input.Key, title);
        RequestFlowRules.RequireUniqueKey(flow.Steps.Select(s => s.Key), key, "Another step");

        var step = new RequestFlowStep
        {
            FlowId = flow.Id,
            Key = key,
            Title = title,
            Kind = input.Kind,
            DisplayOrder = (flow.Steps.Count == 0 ? 0 : flow.Steps.Max(s => s.DisplayOrder)) + 1,
            TaskDepartmentId = input.Kind == RequestStepKind.Task ? flow.Category.DepartmentId : null
        };
        db.RequestFlowSteps.Add(step);
        flow.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestFlow, flow.Id, AuditAction.Updated, flow.Category.DepartmentId, flow.Title,
            new { stepAdded = new { step.Key, step.Title, kind = step.Kind } });
        await db.SaveChangesAsync(ct);
        return step;
    }

    /// <summary>
    /// Save a step's settings: the common ones, its dependencies, and the ones its kind uses. Templates may only use the tokens of the
    /// request and of the steps this one depends on; field references must point at fields of those steps, of the right type.
    /// </summary>
    public async Task<RequestFlowStep> UpdateStepAsync(Guid stepId, RequestStepInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var flowId = await db.RequestFlowSteps.Where(s => s.Id == stepId).Select(s => (Guid?)s.FlowId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Request step not found.");
        var flow = await LoadFlow(db.RequestFlows).AsSplitQuery().FirstAsync(f => f.Id == flowId, ct);
        RequireConfigure(actor, flow.Category.DepartmentId);
        Link(flow);
        var step = flow.Steps.First(s => s.Id == stepId);
        var changes = new ChangeSet();

        var title = RequestFlowRules.RequireTitle(input.Title, RequestFlowRules.MaxStepTitleLength);
        var key = RequestFlowRules.CleanKey(input.Key, title);
        if (key != step.Key)
        {
            RequestFlowRules.RequireUniqueKey(flow.Steps.Where(s => s.Id != step.Id).Select(s => s.Key), key, "Another step");
            await RequireNoRequestsInProgressAsync(flow.Id, "a step's key", ct);
        }
        changes.TrackText("title", step.Title, title).Track("key", step.Key, key);

        // Dependencies: the step's own rows replaced; the whole flow's checked together.
        var byId = flow.Steps.ToDictionary(s => s.Id);
        var wanted = input.Dependencies
            .GroupBy(d => d.DependsOnStepId).Select(g => g.First())
            .Select(d => new RequestFlowStepDependency { StepId = step.Id, Step = step, DependsOnStepId = d.DependsOnStepId, DependsOnStep = byId.GetValueOrDefault(d.DependsOnStepId)!, RequiredOutcome = d.RequiredOutcome })
            .ToList();
        var others = flow.Steps.Where(s => s.Id != step.Id).SelectMany(s => s.Dependencies).ToList();
        RequestFlowRules.ValidateDependencies(flow.Steps.ToList(), [.. others, .. wanted]);
        var before = step.Dependencies.OrderBy(d => d.DependsOnStepId).Select(d => $"{byId[d.DependsOnStepId].Key}{Outcome(d.RequiredOutcome)}").ToList();
        var after = wanted.OrderBy(d => d.DependsOnStepId).Select(d => $"{byId[d.DependsOnStepId].Key}{Outcome(d.RequiredOutcome)}").ToList();
        if (!before.SequenceEqual(after))
        {
            await RequireNoRequestsInProgressAsync(flow.Id, "a step's dependencies", ct);
            changes.Track("dependsOn", string.Join(", ", before), string.Join(", ", after));
            db.RequestFlowStepDependencies.RemoveRange(step.Dependencies);
            step.Dependencies = wanted;
            db.RequestFlowStepDependencies.AddRange(wanted);
        }

        var predecessorIds = RequestFlowRules.TransitivePredecessors(step.Id, [.. others, .. wanted]);
        var predecessors = flow.Steps.Where(s => predecessorIds.Contains(s.Id)).ToList();
        var tokens = RequestTokenRules.Available(predecessors).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var predecessorFields = predecessors.Where(s => s.Kind == RequestStepKind.Form).SelectMany(s => s.Fields).ToDictionary(f => f.Id);

        switch (step.Kind)
        {
            case RequestStepKind.Form:
            case RequestStepKind.Url:
                var performer = await CleanPerformerAsync(input.PerformedById, ct);
                changes.Track("performedById", step.PerformedById, performer);
                step.PerformedById = performer;
                if (step.Kind == RequestStepKind.Url)
                {
                    var url = RequestFlowRules.CleanUrl(input.Url);
                    RequestTokenRules.Validate(url, tokens, "The web address");
                    changes.TrackText("url", step.Url, url);
                    step.Url = url;
                }
                break;

            case RequestStepKind.Task:
                var departmentId = input.TaskDepartmentId ?? throw new ValidationException("Choose the department the task is filed in.");
                await RequireOpenDepartmentAsync(departmentId, ct);
                if (!Enum.IsDefined(input.TaskType)) throw new ValidationException("Choose one of the task types offered.");
                if (!Enum.IsDefined(input.TaskPriority)) throw new ValidationException("Choose one of the priorities offered.");
                var titleTemplate = RequestFlowRules.Clean(input.TitleTemplate, RequestFlowRules.MaxTitleTemplateLength, "The task title")
                    ?? throw new ValidationException("The task needs a title; it may use values from earlier steps.");
                var descriptionTemplate = RequestFlowRules.Clean(input.DescriptionTemplate, RequestFlowRules.MaxTemplateLength, "The task description");
                RequestTokenRules.Validate(titleTemplate, tokens, "The task title");
                RequestTokenRules.Validate(descriptionTemplate, tokens, "The task description");
                var dueField = FieldRef(input.DueDateFieldId, predecessorFields, RequestFieldType.Date, "the due date");
                var assetField = FieldRef(input.AssetFieldId, predecessorFields, RequestFieldType.Asset, "the asset");
                var projectField = FieldRef(input.ProjectFieldId, predecessorFields, RequestFieldType.Project, "the project");
                var priorityField = FieldRef(input.PriorityFieldId, predecessorFields, RequestFieldType.Urgency, "the priority");
                if (!Enum.IsDefined(input.RequesteeSource)) throw new ValidationException("Choose whom the task is for.");
                Guid? requesteeField = null;
                if (input.RequesteeSource == RequesteeSource.Field)
                    requesteeField = FieldRef(input.RequesteeFieldId, predecessorFields, RequestFieldType.User, "who it is for")
                        ?? throw new ValidationException("Choose the Person field that names whom the task is for.");
                Guid? copyFrom = null;
                if (!input.CopyAllAttachments && input.CopyAttachmentsFromStepId is Guid copyId)
                {
                    if (!predecessorIds.Contains(copyId) || byId[copyId].Kind != RequestStepKind.Form)
                        throw new ValidationException("Files can only be copied from a form this step depends on.");
                    copyFrom = copyId;
                }
                var assigneeIds = await CleanAssigneesAsync(input.AssigneeIds, ct);
                changes.Track("taskDepartmentId", step.TaskDepartmentId, departmentId)
                    .Track("taskType", step.TaskType, input.TaskType)
                    .Track("taskPriority", step.TaskPriority, input.TaskPriority)
                    .TrackText("titleTemplate", step.TitleTemplate, titleTemplate)
                    .TrackText("descriptionTemplate", step.DescriptionTemplate, descriptionTemplate)
                    .Track("dueDateFieldId", step.DueDateFieldId, dueField)
                    .Track("assetFieldId", step.AssetFieldId, assetField)
                    .Track("projectFieldId", step.ProjectFieldId, projectField)
                    .Track("priorityFieldId", step.PriorityFieldId, priorityField)
                    .Track("requesteeSource", step.RequesteeSource, input.RequesteeSource)
                    .Track("requesteeFieldId", step.RequesteeFieldId, requesteeField)
                    .Track("copyAllAttachments", step.CopyAllAttachments, input.CopyAllAttachments)
                    .Track("copyAttachmentsFromStepId", step.CopyAttachmentsFromStepId, copyFrom)
                    .Track("assignees", string.Join(",", step.Assignees.Select(a => a.UserId).Order()), string.Join(",", assigneeIds.Order()));
                step.TaskDepartmentId = departmentId;
                step.TaskType = input.TaskType;
                step.TaskPriority = input.TaskPriority;
                step.TitleTemplate = titleTemplate;
                step.DescriptionTemplate = descriptionTemplate;
                step.DueDateFieldId = dueField;
                step.AssetFieldId = assetField;
                step.ProjectFieldId = projectField;
                step.PriorityFieldId = priorityField;
                step.RequesteeSource = input.RequesteeSource;
                step.RequesteeFieldId = requesteeField;
                step.CopyAllAttachments = input.CopyAllAttachments;
                step.CopyAttachmentsFromStepId = copyFrom;
                db.RequestFlowStepAssignees.RemoveRange(step.Assignees.Where(a => !assigneeIds.Contains(a.UserId)));
                foreach (var userId in assigneeIds.Where(id => step.Assignees.All(a => a.UserId != id)))
                    db.RequestFlowStepAssignees.Add(new RequestFlowStepAssignee { StepId = step.Id, UserId = userId });
                break;

            case RequestStepKind.Action:
                var actionId = input.ActionId ?? throw new ValidationException("Choose the action this step runs.");
                var action = await db.RequestActions.AsNoTracking().Include(a => a.Parameters).FirstOrDefaultAsync(a => a.Id == actionId, ct)
                    ?? throw new ValidationException("That action doesn't exist.");
                var inputs = new Dictionary<string, string?>();
                foreach (var p in action.Parameters.OrderBy(p => p.DisplayOrder))
                {
                    var template = RequestFlowRules.Clean(input.ActionInputs.GetValueOrDefault(p.Key), RequestFlowRules.MaxTemplateLength, $"\"{p.Label}\"");
                    RequestTokenRules.Validate(template, tokens, $"\"{p.Label}\"");
                    inputs[p.Key] = template;
                }
                changes.Track("actionId", step.ActionId, actionId);
                foreach (var (k, v) in inputs)
                    changes.TrackText($"input.{k}", step.ActionInputs.FirstOrDefault(i => i.ParameterKey == k)?.ValueTemplate, v);
                step.ActionId = actionId;
                db.RequestFlowStepActionInputs.RemoveRange(step.ActionInputs.Where(i => !inputs.ContainsKey(i.ParameterKey)));
                foreach (var (k, v) in inputs)
                {
                    var row = step.ActionInputs.FirstOrDefault(i => i.ParameterKey == k);
                    if (row is null) db.RequestFlowStepActionInputs.Add(new RequestFlowStepActionInput { StepId = step.Id, ParameterKey = k, ValueTemplate = v });
                    else row.ValueTemplate = v;
                }
                break;
        }

        step.Title = title;
        step.Key = key;
        flow.UpdatedAt = DateTime.UtcNow;
        if (changes.HasChanges)
            audit.Add(actor, AuditEntity.RequestFlow, flow.Id, AuditAction.Updated, flow.Category.DepartmentId, flow.Title,
                new { stepChanged = step.Key, changes = changes.Changes });
        await db.SaveChangesAsync(ct);
        return step;
    }

    /// <summary>Delete a step - only while no request was ever logged through the flow. The dependencies on it go with it.</summary>
    public async Task<Guid> DeleteStepAsync(Guid stepId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await db.RequestFlowSteps.Include(s => s.Flow).ThenInclude(f => f.Category).FirstOrDefaultAsync(s => s.Id == stepId, ct)
            ?? throw new NotFoundException("Request step not found.");
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        await RequireNeverLoggedAsync(step.FlowId, "a step", ct);
        db.RequestFlowSteps.Remove(step);
        step.Flow.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { stepRemoved = new { step.Key, step.Title, kind = step.Kind } });
        await db.SaveChangesAsync(ct);
        return step.FlowId;
    }

    public async Task MoveStepAsync(Guid stepId, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await db.RequestFlowSteps.AsNoTracking().Include(s => s.Flow).ThenInclude(f => f.Category).FirstOrDefaultAsync(s => s.Id == stepId, ct)
            ?? throw new NotFoundException("Request step not found.");
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        var siblings = await db.RequestFlowSteps.Where(s => s.FlowId == step.FlowId).ToListAsync(ct);
        if (Reorder(siblings, s => s.Id, s => s.DisplayOrder, s => s.Title, (s, n) => s.DisplayOrder = n, stepId, direction))
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- fields

    public async Task<RequestFormField> AddFieldAsync(Guid stepId, RequestFieldInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await LoadFormStepAsync(stepId, ct);
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        if (step.Fields.Count >= RequestFlowRules.MaxFields) throw new ValidationException($"A form can have at most {RequestFlowRules.MaxFields} fields.");
        var def = RequestFlowRules.ValidateField(input);
        RequestFlowRules.RequireUniqueKey(step.Fields.Select(f => f.Key), def.Key, "Another field of this form");
        await RequireNoRequestsInProgressAsync(step.FlowId, "a form's fields", ct);
        var field = new RequestFormField
        {
            StepId = step.Id,
            Key = def.Key,
            Prompt = def.Prompt,
            HelpText = def.HelpText,
            FieldType = def.Type,
            IsRequired = def.IsRequired,
            Choices = [.. def.Choices],
            PickerScope = def.Scope,
            DisplayOrder = (step.Fields.Count == 0 ? 0 : step.Fields.Max(f => f.DisplayOrder)) + 1
        };
        db.RequestFormFields.Add(field);
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, fieldAdded = Describe(field) });
        await db.SaveChangesAsync(ct);
        return field;
    }

    public async Task UpdateFieldAsync(Guid stepId, Guid fieldId, RequestFieldInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await LoadFormStepAsync(stepId, ct);
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        var field = step.Fields.FirstOrDefault(f => f.Id == fieldId) ?? throw new NotFoundException("Field not found.");
        var def = RequestFlowRules.ValidateField(input, string.IsNullOrWhiteSpace(input.Key) ? field.Key : null);
        if (def.Key != field.Key)
            RequestFlowRules.RequireUniqueKey(step.Fields.Where(f => f.Id != fieldId).Select(f => f.Key), def.Key, "Another field of this form");
        if (def.Key != field.Key || def.Type != field.FieldType)
            await RequireNoRequestsInProgressAsync(step.FlowId, "a field's key or type", ct);
        var changes = new ChangeSet()
            .Track("key", field.Key, def.Key)
            .TrackText("prompt", field.Prompt, def.Prompt)
            .TrackText("helpText", field.HelpText, def.HelpText)
            .Track("type", field.FieldType, def.Type)
            .Track("required", field.IsRequired, def.IsRequired)
            .Track("choices", string.Join("\n", field.Choices), string.Join("\n", def.Choices))
            .Track("scope", field.PickerScope, def.Scope);
        if (!changes.HasChanges) return;
        field.Key = def.Key;
        field.Prompt = def.Prompt;
        field.HelpText = def.HelpText;
        field.FieldType = def.Type;
        field.IsRequired = def.IsRequired;
        field.Choices = [.. def.Choices];
        field.PickerScope = def.Scope;
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, fieldChanged = field.Key, changes = changes.Changes });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteFieldAsync(Guid stepId, Guid fieldId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await LoadFormStepAsync(stepId, ct);
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        var field = step.Fields.FirstOrDefault(f => f.Id == fieldId) ?? throw new NotFoundException("Field not found.");
        if (await db.RequestFormAnswers.AnyAsync(a => a.FieldId == fieldId, ct))
            throw new ValidationException("This field has been answered in requests already logged, so it can't be deleted. Archive the flow and build a new one instead.");
        db.RequestFormFields.Remove(field);
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, fieldRemoved = Describe(field) });
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveFieldAsync(Guid stepId, Guid fieldId, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await LoadFormStepAsync(stepId, ct);
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        if (Reorder(step.Fields.ToList(), f => f.Id, f => f.DisplayOrder, f => f.Prompt, (f, n) => f.DisplayOrder = n, fieldId, direction))
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- approval stages and approvers

    public async Task<RequestFlowApprovalStage> AddStageAsync(Guid stepId, ApprovalRule rule, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var step = await LoadApprovalStepAsync(stepId, ct);
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        if (!Enum.IsDefined(rule)) throw new ValidationException("Choose one of the rules offered.");
        if (step.Stages.Count >= RequestFlowRules.MaxStages) throw new ValidationException($"An approval can have at most {RequestFlowRules.MaxStages} stages.");
        var stage = new RequestFlowApprovalStage { StepId = step.Id, StageOrder = step.Stages.Count + 1, Rule = rule };
        db.RequestFlowApprovalStages.Add(stage);
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, stageAdded = new { stage.StageOrder, rule } });
        await db.SaveChangesAsync(ct);
        return stage;
    }

    public async Task UpdateStageAsync(Guid stageId, ApprovalRule rule, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var stage = await LoadStageAsync(stageId, ct);
        RequireConfigure(actor, stage.Step.Flow.Category.DepartmentId);
        if (!Enum.IsDefined(rule)) throw new ValidationException("Choose one of the rules offered.");
        if (stage.Rule == rule) return;
        var from = stage.Rule;
        stage.Rule = rule;
        Touch(stage.Step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, stage.Step.FlowId, AuditAction.Updated, stage.Step.Flow.Category.DepartmentId, stage.Step.Flow.Title,
            new { step = stage.Step.Key, stageChanged = stage.StageOrder, rule = new { from, to = rule } });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> DeleteStageAsync(Guid stageId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var stage = await LoadStageAsync(stageId, ct);
        RequireConfigure(actor, stage.Step.Flow.Category.DepartmentId);
        var step = stage.Step;
        db.RequestFlowApprovalStages.Remove(stage);
        var n = 1;
        foreach (var s in step.Stages.Where(s => s.Id != stageId).OrderBy(s => s.StageOrder)) s.StageOrder = n++;
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, stageRemoved = stage.StageOrder });
        await db.SaveChangesAsync(ct);
        return step.Id;
    }

    public async Task MoveStageAsync(Guid stageId, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var stage = await LoadStageAsync(stageId, ct);
        RequireConfigure(actor, stage.Step.Flow.Category.DepartmentId);
        if (Reorder(stage.Step.Stages.ToList(), s => s.Id, s => s.StageOrder, s => s.Id.ToString(), (s, n) => s.StageOrder = n, stageId, direction))
            await db.SaveChangesAsync(ct);
    }

    public async Task<RequestFlowApprover> AddApproverAsync(Guid stageId, RequestApproverInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var stage = await LoadStageAsync(stageId, ct);
        RequireConfigure(actor, stage.Step.Flow.Category.DepartmentId);
        RequestFlowRules.ValidateApprover(input);
        if (stage.Approvers.Count >= RequestFlowRules.MaxApproversPerStage) throw new ValidationException($"A stage can have at most {RequestFlowRules.MaxApproversPerStage} approvers.");
        var approver = new RequestFlowApprover { StageId = stage.Id, Kind = input.Kind };
        string what;
        switch (input.Kind)
        {
            case ApproverKind.Person:
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == input.UserId, ct) ?? throw new ValidationException("That person doesn't exist.");
                if (!user.IsActive || user.IsSystemAccount) throw new ValidationException("An approver must be an active person.");
                if (stage.Approvers.Any(a => a.Kind == ApproverKind.Person && a.UserId == user.Id)) throw new ValidationException($"{user.DisplayName} is already an approver of this stage.");
                approver.UserId = user.Id;
                what = user.DisplayName;
                break;
            case ApproverKind.RoleInDepartment:
                var role = await RequireRoleAsync(input.RoleId, ct);
                var dept = await RequireOpenDepartmentAsync(input.DepartmentId!.Value, ct);
                if (stage.Approvers.Any(a => a.Kind == ApproverKind.RoleInDepartment && a.RoleId == role.Id && a.DepartmentId == dept.Id))
                    throw new ValidationException($"{role.Name} in {dept.Name} is already an approver of this stage.");
                approver.RoleId = role.Id;
                approver.DepartmentId = dept.Id;
                what = $"{role.Name} in {dept.Name}";
                break;
            default:
                var r = await RequireRoleAsync(input.RoleId, ct);
                if (stage.Approvers.Any(a => a.Kind == ApproverKind.RoleInRequestersDepartment && a.RoleId == r.Id))
                    throw new ValidationException($"{r.Name} in the requester's department is already an approver of this stage.");
                approver.RoleId = r.Id;
                what = $"{r.Name} in the requester's department";
                break;
        }
        db.RequestFlowApprovers.Add(approver);
        Touch(stage.Step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, stage.Step.FlowId, AuditAction.Updated, stage.Step.Flow.Category.DepartmentId, stage.Step.Flow.Title,
            new { step = stage.Step.Key, stage = stage.StageOrder, approverAdded = what, kind = input.Kind });
        await db.SaveChangesAsync(ct);
        return approver;
    }

    public async Task<Guid> RemoveApproverAsync(Guid approverId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var approver = await db.RequestFlowApprovers
            .Include(a => a.User).Include(a => a.Department).Include(a => a.Role)
            .Include(a => a.Stage).ThenInclude(s => s.Step).ThenInclude(st => st.Flow).ThenInclude(f => f.Category)
            .FirstOrDefaultAsync(a => a.Id == approverId, ct) ?? throw new NotFoundException("Approver not found.");
        var step = approver.Stage.Step;
        RequireConfigure(actor, step.Flow.Category.DepartmentId);
        db.RequestFlowApprovers.Remove(approver);
        Touch(step.Flow);
        audit.Add(actor, AuditEntity.RequestFlow, step.FlowId, AuditAction.Updated, step.Flow.Category.DepartmentId, step.Flow.Title,
            new { step = step.Key, stage = approver.Stage.StageOrder, approverRemoved = ApproverLabel(approver), kind = approver.Kind });
        await db.SaveChangesAsync(ct);
        return step.Id;
    }

    /// <summary>"Jane Smith", "Department Admin in Finance", "Department Admin in the requester's department".</summary>
    public static string ApproverLabel(RequestFlowApprover a) => a.Kind switch
    {
        ApproverKind.Person => a.User?.DisplayName ?? "a person",
        ApproverKind.RoleInDepartment => $"{a.Role?.Name ?? "a role"} in {a.Department?.Name ?? "a department"}",
        _ => $"{a.Role?.Name ?? "a role"} in the requester's department"
    };

    // ---------------------------------------------------------------- helpers

    private static IQueryable<RequestCategory> LoadCategory(IQueryable<RequestCategory> q) => q
        .Include(c => c.Department)
        .Include(c => c.Flows).ThenInclude(f => f.Steps).ThenInclude(s => s.Fields)
        .Include(c => c.Flows).ThenInclude(f => f.Steps).ThenInclude(s => s.Stages).ThenInclude(st => st.Approvers)
        .Include(c => c.Flows).ThenInclude(f => f.Steps).ThenInclude(s => s.Dependencies)
        .Include(c => c.Flows).ThenInclude(f => f.Steps).ThenInclude(s => s.Action).ThenInclude(a => a!.Parameters)
        .Include(c => c.Flows).ThenInclude(f => f.Steps).ThenInclude(s => s.ActionInputs);

    private static IQueryable<RequestFlow> LoadFlow(IQueryable<RequestFlow> q) => q
        .Include(f => f.Category).ThenInclude(c => c.Department)
        .Include(f => f.Steps).ThenInclude(s => s.Fields)
        .Include(f => f.Steps).ThenInclude(s => s.Dependencies)
        .Include(f => f.Steps).ThenInclude(s => s.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.User)
        .Include(f => f.Steps).ThenInclude(s => s.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.Department)
        .Include(f => f.Steps).ThenInclude(s => s.Stages).ThenInclude(st => st.Approvers).ThenInclude(a => a.Role)
        .Include(f => f.Steps).ThenInclude(s => s.Assignees).ThenInclude(a => a.User)
        .Include(f => f.Steps).ThenInclude(s => s.PerformedBy)
        .Include(f => f.Steps).ThenInclude(s => s.TaskDepartment)
        .Include(f => f.Steps).ThenInclude(s => s.Action).ThenInclude(a => a!.Parameters)
        .Include(f => f.Steps).ThenInclude(s => s.ActionInputs);

    /// <summary>Point each loaded flow back at its category, so <see cref="RequestFlowRules.IsLive"/> can read it, and order flows and steps.</summary>
    private static RequestCategory Link(RequestCategory category)
    {
        category.Flows = category.Flows.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Title).ToList();
        foreach (var f in category.Flows)
        {
            f.Category = category;
            Link(f);
        }
        return category;
    }

    private static RequestFlow Link(RequestFlow flow)
    {
        flow.Steps = flow.Steps.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Title).ToList();
        foreach (var s in flow.Steps)
        {
            s.Flow = flow;
            s.Fields = s.Fields.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Prompt).ToList();
            s.Stages = s.Stages.OrderBy(st => st.StageOrder).ToList();
        }
        return flow;
    }

    private async Task<RequestFlowStep> LoadFormStepAsync(Guid stepId, CancellationToken ct)
    {
        var step = await db.RequestFlowSteps.Include(s => s.Flow).ThenInclude(f => f.Category).Include(s => s.Fields)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct) ?? throw new NotFoundException("Request step not found.");
        if (step.Kind != RequestStepKind.Form) throw new ValidationException("Only a form has fields.");
        return step;
    }

    private async Task<RequestFlowStep> LoadApprovalStepAsync(Guid stepId, CancellationToken ct)
    {
        var step = await db.RequestFlowSteps.Include(s => s.Flow).ThenInclude(f => f.Category).Include(s => s.Stages).ThenInclude(st => st.Approvers)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct) ?? throw new NotFoundException("Request step not found.");
        if (step.Kind != RequestStepKind.Approval) throw new ValidationException("Only an approval has stages.");
        return step;
    }

    private async Task<RequestFlowApprovalStage> LoadStageAsync(Guid stageId, CancellationToken ct) =>
        await db.RequestFlowApprovalStages
            .Include(s => s.Approvers)
            .Include(s => s.Step).ThenInclude(st => st.Stages)
            .Include(s => s.Step).ThenInclude(st => st.Flow).ThenInclude(f => f.Category)
            .FirstOrDefaultAsync(s => s.Id == stageId, ct) ?? throw new NotFoundException("Approval stage not found.");

    private static object Describe(RequestFormField f) =>
        new { f.Key, f.Prompt, type = f.FieldType, required = f.IsRequired, choices = f.Choices.Count == 0 ? null : f.Choices, scope = f.PickerScope };

    private static string Outcome(RequestOutcome? outcome) => outcome is null ? "" : $" ({outcome.ToString()!.ToLowerInvariant()})";

    private static void Touch(RequestFlow flow) => flow.UpdatedAt = DateTime.UtcNow;

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A field of a predecessor form of the given type, or null for none; refused when it is anything else.</summary>
    private static Guid? FieldRef(Guid? fieldId, IReadOnlyDictionary<Guid, RequestFormField> predecessorFields, RequestFieldType type, string what)
    {
        if (fieldId is not Guid id) return null;
        if (!predecessorFields.TryGetValue(id, out var field))
            throw new ValidationException($"The field for {what} must belong to a form this step depends on.");
        if (field.FieldType != type)
            throw new ValidationException($"The field for {what} must be of type {type.Label()}; \"{field.Prompt}\" is of type {field.FieldType.Label()}.");
        return id;
    }

    private async Task<Guid?> CleanPerformerAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is not Guid id || id == Guid.Empty) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new ValidationException("That person doesn't exist.");
        if (!user.IsActive || user.IsSystemAccount) throw new ValidationException("A step can only be addressed to an active person.");
        return id;
    }

    private async Task<IReadOnlyList<Guid>> CleanAssigneesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return [];
        var found = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).Select(u => new { u.Id, u.IsActive, u.IsSystemAccount }).ToListAsync(ct);
        if (found.Count != wanted.Count) throw new ValidationException("One of the assignees doesn't exist.");
        if (found.Any(u => !u.IsActive || u.IsSystemAccount)) throw new ValidationException("An assignee must be an active person.");
        return wanted;
    }

    private async Task<ApplicationRole> RequireRoleAsync(Guid? roleId, CancellationToken ct) =>
        await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new ValidationException("That role doesn't exist.");

    private static void RequireConfigure(Actor actor, Guid departmentId) =>
        AccessPolicy.Require(AccessPolicy.CanConfigureRequestsIn(actor, departmentId), "You don't have permission to configure this department's request flows.");

    private async Task<Department> RequireOpenDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new NotFoundException("Department not found.");
        if (dept.IsArchived) throw new ValidationException($"Department \"{dept.Name}\" is archived.");
        return dept;
    }

    private async Task RequireUniqueCategoryTitleAsync(Guid departmentId, string title, Guid? exceptId, CancellationToken ct)
    {
        var t = title.ToLowerInvariant();
        if (await db.RequestCategories.AnyAsync(c => c.DepartmentId == departmentId && c.Title.ToLower() == t && c.Id != exceptId, ct))
            throw new ValidationException($"The department already has a request category called \"{title}\".");
    }

    private static void RequireUniqueFlowTitle(RequestCategory category, string title, Guid? exceptId)
    {
        if (category.Flows.Any(f => f.Id != exceptId && string.Equals(f.Title, title, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"\"{category.Title}\" already has a flow called \"{title}\".");
    }

    private async Task RequireNoRequestsInProgressAsync(Guid flowId, string what, CancellationToken ct)
    {
        var count = await InProgressRequestCountAsync(flowId, ct);
        if (count > 0)
            throw new ValidationException($"{Count(count, "request")} in progress {(count == 1 ? "uses" : "use")} this flow, so {what} can't change until {(count == 1 ? "it finishes" : "they finish")}. For a bigger change, archive the flow and build a new one.");
    }

    private async Task RequireNeverLoggedAsync(Guid flowId, string what, CancellationToken ct)
    {
        var count = await db.Requests.CountAsync(r => r.FlowId == flowId, ct);
        if (count > 0)
            throw new ValidationException($"{Count(count, "request")} {(count == 1 ? "was" : "were")} logged through this flow, so {what} can't be deleted. Archive the flow and build a new one instead.");
    }

    /// <summary>Swap one item with its neighbour in display order and renumber them all from 1. False when it is already at that end.</summary>
    private static bool Reorder<T>(List<T> items, Func<T, Guid> id, Func<T, int> order, Func<T, string> name, Action<T, int> setOrder, Guid moving, int direction)
    {
        var ordered = items.OrderBy(order).ThenBy(name).ToList();
        var index = ordered.FindIndex(x => id(x) == moving);
        if (index < 0) throw new NotFoundException("Not found.");
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= ordered.Count) return false;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) setOrder(ordered[i], i + 1);
        return true;
    }
}

using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Configuring request flows (spec §6.20): a department's categories, their options and each flow's questions. Everything belongs to the
/// category's department, set when the category is created and never changed; requests.configure reaching that department manages it.
/// Every change is audited on the category or on the option (question changes are recorded on their option).
/// </summary>
public sealed class RequestCatalogueService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    /// <summary>The categories within the caller's requests.configure reach, by department then display order, with their options and questions.</summary>
    public async Task<IReadOnlyList<RequestCategory>> ListAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var categories = await Scoping.RequestCategories(Load(db.RequestCategories.AsNoTracking()), actor)
            .OrderBy(c => c.Department.Name).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Title)
            .AsSplitQuery()
            .ToListAsync(ct);
        foreach (var c in categories) Link(c);
        return categories;
    }

    /// <summary>A category for its edit page, with its department, options and their questions.</summary>
    public async Task<RequestCategory> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await Load(db.RequestCategories.AsNoTracking()).AsSplitQuery().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        return Link(category);
    }

    /// <summary>An option for its edit page, with its category, the category's department and its questions in order.</summary>
    public async Task<RequestOption> GetOptionAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await db.RequestOptions.AsNoTracking()
            .Include(o => o.Category).ThenInclude(c => c.Department)
            .Include(o => o.Questions)
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Request option not found.");
        RequireConfigure(actor, option.Category.DepartmentId);
        return option;
    }

    /// <summary>Whether the caller may configure a department's request flows - for the Configure buttons on the Requests pages.</summary>
    public async Task<bool> CanConfigureAsync(Guid departmentId, CancellationToken ct = default) =>
        AccessPolicy.CanConfigureRequestsIn(await actors.GetAsync(ct), departmentId);

    // ---------------------------------------------------------------- categories

    public async Task<RequestCategory> CreateCategoryAsync(RequestCategoryInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var departmentId = input.DepartmentId ?? actor.DepartmentId
            ?? throw new ValidationException("A department is required (your role isn't scoped to one, so choose it explicitly).");
        RequireConfigure(actor, departmentId);
        var dept = await RequireOpenDepartmentAsync(departmentId, ct);
        var title = RequestRules.RequireTitle(input.Title, RequestRules.MaxCategoryTitleLength);
        await RequireUniqueCategoryTitleAsync(departmentId, title, null, ct);

        var now = DateTime.UtcNow;
        var last = await db.RequestCategories.Where(c => c.DepartmentId == departmentId).MaxAsync(c => (int?)c.DisplayOrder, ct) ?? 0;
        var category = new RequestCategory
        {
            DepartmentId = departmentId,
            Title = title,
            Description = RequestRules.Clean(input.Description, RequestRules.MaxDescriptionLength, "The description"),
            Icon = RequestRules.CleanIcon(input.Icon),
            Colour = RequestRules.CleanColour(input.Colour),
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
        var title = RequestRules.RequireTitle(input.Title, RequestRules.MaxCategoryTitleLength);
        if (!string.Equals(title, category.Title, StringComparison.OrdinalIgnoreCase)) await RequireUniqueCategoryTitleAsync(category.DepartmentId, title, id, ct);
        var description = RequestRules.Clean(input.Description, RequestRules.MaxDescriptionLength, "The description");
        var icon = RequestRules.CleanIcon(input.Icon);
        var colour = RequestRules.CleanColour(input.Colour);
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

    /// <summary>Archive: the category and every option under it leave the Requests page; nothing is deleted.</summary>
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

    /// <summary>Delete a category with its options and their questions. Requests already logged keep their questions and answers. Returns how many options went.</summary>
    public async Task<int> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.Include(c => c.Options).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var options = category.Options.Count;
        db.RequestCategories.Remove(category); // options and questions cascade
        audit.Add(actor, AuditEntity.RequestCategory, category.Id, AuditAction.Deleted, category.DepartmentId, category.Title,
            new { category.Title, options = category.Options.Select(o => o.Title).ToList() });
        await db.SaveChangesAsync(ct);
        return options;
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

    // ---------------------------------------------------------------- options

    public async Task<RequestOption> AddOptionAsync(Guid categoryId, RequestOptionInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var category = await db.RequestCategories.Include(c => c.Options).FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new NotFoundException("Request category not found.");
        RequireConfigure(actor, category.DepartmentId);
        var (title, description, url) = RequestRules.ValidateOption(input);
        RequireUniqueOptionTitle(category, title, null);

        var now = DateTime.UtcNow;
        var option = new RequestOption
        {
            CategoryId = category.Id,
            Title = title,
            Description = description,
            Kind = input.Kind,
            Url = url,
            AllowAttachments = input.AllowAttachments,
            TaskType = input.TaskType,
            DisplayOrder = category.Options.Count == 0 ? 1 : category.Options.Max(o => o.DisplayOrder) + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RequestOptions.Add(option);
        category.UpdatedAt = now;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Created, category.DepartmentId, option.Title,
            new { option.Title, option.Kind, option.Url, option.TaskType, option.AllowAttachments, category = category.Title });
        await db.SaveChangesAsync(ct);
        return option;
    }

    /// <summary>
    /// Change an option's fields. Switching a Flow to a Link keeps its questions (unused while it is a link), so switching back
    /// restores them.
    /// </summary>
    public async Task<RequestOption> UpdateOptionAsync(Guid id, RequestOptionInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await db.RequestOptions.Include(o => o.Category).ThenInclude(c => c.Options).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Request option not found.");
        RequireConfigure(actor, option.Category.DepartmentId);
        var (title, description, url) = RequestRules.ValidateOption(input);
        RequireUniqueOptionTitle(option.Category, title, id);
        var changes = new ChangeSet()
            .TrackText("title", option.Title, title)
            .TrackText("description", option.Description, description)
            .Track("kind", option.Kind, input.Kind)
            .TrackText("url", option.Url, url)
            .Track("allowAttachments", option.AllowAttachments, input.AllowAttachments)
            .Track("taskType", option.TaskType, input.TaskType);
        if (!changes.HasChanges) return option;
        option.Title = title;
        option.Description = description;
        option.Kind = input.Kind;
        option.Url = url;
        option.AllowAttachments = input.AllowAttachments;
        option.TaskType = input.TaskType;
        option.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Updated, option.Category.DepartmentId, option.Title, changes.Changes);
        await db.SaveChangesAsync(ct);
        return option;
    }

    public async Task SetOptionArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await db.RequestOptions.Include(o => o.Category).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Request option not found.");
        RequireConfigure(actor, option.Category.DepartmentId);
        if (option.IsArchived == archived) return;
        option.IsArchived = archived;
        option.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, archived ? AuditAction.Archived : AuditAction.Unarchived, option.Category.DepartmentId, option.Title);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete an option and its questions. Returns its category's id, for the page to go back to.</summary>
    public async Task<Guid> DeleteOptionAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await db.RequestOptions.Include(o => o.Category).Include(o => o.Questions).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Request option not found.");
        RequireConfigure(actor, option.Category.DepartmentId);
        db.RequestOptions.Remove(option); // its questions cascade
        option.Category.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Deleted, option.Category.DepartmentId, option.Title,
            new { option.Title, category = option.Category.Title, questions = option.Questions.Count });
        await db.SaveChangesAsync(ct);
        return option.CategoryId;
    }

    /// <summary>Move an option one place up (-1) or down (+1) within its category.</summary>
    public async Task MoveOptionAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await db.RequestOptions.Include(o => o.Category).ThenInclude(c => c.Options).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Request option not found.");
        RequireConfigure(actor, option.Category.DepartmentId);
        if (Reorder(option.Category.Options.ToList(), o => o.Id, o => o.DisplayOrder, o => o.Title, (o, n) => o.DisplayOrder = n, id, direction))
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- questions

    public async Task<RequestQuestion> AddQuestionAsync(Guid optionId, RequestQuestionInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await LoadForQuestionsAsync(optionId, ct);
        RequireConfigure(actor, option.Category.DepartmentId);
        var d = RequestRules.ValidateQuestion(input);
        var question = new RequestQuestion
        {
            OptionId = option.Id,
            Prompt = d.Prompt,
            HelpText = d.HelpText,
            QuestionType = d.Type,
            IsRequired = d.IsRequired,
            Choices = d.Choices,
            SetsDueDate = d.SetsDueDate,
            DisplayOrder = option.Questions.Count == 0 ? 1 : option.Questions.Max(q => q.DisplayOrder) + 1
        };
        RequestRules.CheckFlow([.. option.Questions, question]);
        db.RequestQuestions.Add(question);
        option.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Updated, option.Category.DepartmentId, option.Title,
            new { questionAdded = Describe(question) });
        await db.SaveChangesAsync(ct);
        return question;
    }

    public async Task<RequestQuestion> UpdateQuestionAsync(Guid optionId, Guid questionId, RequestQuestionInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await LoadForQuestionsAsync(optionId, ct);
        RequireConfigure(actor, option.Category.DepartmentId);
        var question = option.Questions.FirstOrDefault(q => q.Id == questionId) ?? throw new NotFoundException("Question not found.");
        var d = RequestRules.ValidateQuestion(input);
        var proposed = new RequestQuestion { Id = question.Id, QuestionType = d.Type, SetsDueDate = d.SetsDueDate };
        RequestRules.CheckFlow(option.Questions.Select(q => q.Id == questionId ? proposed : q).ToList());

        var changes = new ChangeSet()
            .TrackText("prompt", question.Prompt, d.Prompt)
            .TrackText("helpText", question.HelpText, d.HelpText)
            .Track("type", question.QuestionType, d.Type)
            .Track("required", question.IsRequired, d.IsRequired)
            .Track("choices", string.Join(" | ", question.Choices), string.Join(" | ", d.Choices))
            .Track("setsDueDate", question.SetsDueDate, d.SetsDueDate);
        if (!changes.HasChanges) return question;
        question.Prompt = d.Prompt;
        question.HelpText = d.HelpText;
        question.QuestionType = d.Type;
        question.IsRequired = d.IsRequired;
        question.Choices = d.Choices;
        question.SetsDueDate = d.SetsDueDate;
        option.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Updated, option.Category.DepartmentId, option.Title,
            new { questionChanged = question.Prompt, changes = changes.Changes });
        await db.SaveChangesAsync(ct);
        return question;
    }

    public async Task DeleteQuestionAsync(Guid optionId, Guid questionId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await LoadForQuestionsAsync(optionId, ct);
        RequireConfigure(actor, option.Category.DepartmentId);
        var question = option.Questions.FirstOrDefault(q => q.Id == questionId) ?? throw new NotFoundException("Question not found.");
        db.RequestQuestions.Remove(question);
        option.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.RequestOption, option.Id, AuditAction.Updated, option.Category.DepartmentId, option.Title,
            new { questionRemoved = question.Prompt });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Move a question one step earlier (-1) or later (+1) in its flow.</summary>
    public async Task MoveQuestionAsync(Guid optionId, Guid questionId, int direction, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var option = await LoadForQuestionsAsync(optionId, ct);
        RequireConfigure(actor, option.Category.DepartmentId);
        if (Reorder(option.Questions.ToList(), q => q.Id, q => q.DisplayOrder, q => q.Prompt, (q, n) => q.DisplayOrder = n, questionId, direction))
        {
            option.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // ---------------------------------------------------------------- the example catalogue

    /// <summary>
    /// Load <see cref="RequestExample"/> into a department that has no request categories yet (archived ones count), as ordinary
    /// categories, options and questions to edit from there. Returns how many categories were created.
    /// </summary>
    public async Task<int> LoadExampleAsync(Guid departmentId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        RequireConfigure(actor, departmentId);
        var dept = await RequireOpenDepartmentAsync(departmentId, ct);
        if (await db.RequestCategories.AnyAsync(c => c.DepartmentId == departmentId, ct))
            throw new ValidationException($"{dept.Name} already has request categories. The example can only be loaded into an empty catalogue.");

        var now = DateTime.UtcNow;
        var order = 0;
        foreach (var example in RequestExample.Catalogue)
        {
            var category = new RequestCategory
            {
                DepartmentId = departmentId, Title = example.Title, Description = example.Description, Icon = example.Icon,
                Colour = example.Colour, DisplayOrder = ++order, CreatedAt = now, UpdatedAt = now
            };
            var optionOrder = 0;
            foreach (var o in example.Options)
            {
                var option = new RequestOption
                {
                    CategoryId = category.Id, Title = o.Title, Description = o.Description, Kind = o.Kind, Url = o.Url,
                    TaskType = TaskType.Task, DisplayOrder = ++optionOrder, CreatedAt = now, UpdatedAt = now
                };
                var questionOrder = 0;
                foreach (var q in o.Questions)
                {
                    option.Questions.Add(new RequestQuestion
                    {
                        OptionId = option.Id, Prompt = q.Prompt, HelpText = q.HelpText, QuestionType = q.Type, IsRequired = q.IsRequired,
                        Choices = q.Choices?.ToList() ?? [], SetsDueDate = q.SetsDueDate, DisplayOrder = ++questionOrder
                    });
                }
                category.Options.Add(option);
            }
            db.RequestCategories.Add(category);
            audit.Add(actor, AuditEntity.RequestCategory, category.Id, AuditAction.Created, departmentId, category.Title,
                new { category.Title, category.Icon, category.Colour, department = dept.Name, fromExample = true, options = category.Options.Select(o => o.Title).ToList() });
        }
        await db.SaveChangesAsync(ct);
        return RequestExample.Catalogue.Count;
    }

    // ---------------------------------------------------------------- helpers

    private static IQueryable<RequestCategory> Load(IQueryable<RequestCategory> q) => q
        .Include(c => c.Department)
        .Include(c => c.Options).ThenInclude(o => o.Questions);

    /// <summary>Point each loaded option back at its category, so <see cref="RequestRules.IsLive"/> can read it, and order the options.</summary>
    private static RequestCategory Link(RequestCategory category)
    {
        category.Options = category.Options.OrderBy(o => o.DisplayOrder).ThenBy(o => o.Title).ToList();
        foreach (var o in category.Options) o.Category = category;
        return category;
    }

    private async Task<RequestOption> LoadForQuestionsAsync(Guid optionId, CancellationToken ct) =>
        await db.RequestOptions.Include(o => o.Category).Include(o => o.Questions).FirstOrDefaultAsync(o => o.Id == optionId, ct)
            ?? throw new NotFoundException("Request option not found.");

    private static object Describe(RequestQuestion q) =>
        new { prompt = q.Prompt, type = q.QuestionType, required = q.IsRequired, choices = q.Choices.Count == 0 ? null : q.Choices, setsDueDate = q.SetsDueDate ? true : (bool?)null };

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

    private static void RequireUniqueOptionTitle(RequestCategory category, string title, Guid? exceptId)
    {
        if (category.Options.Any(o => o.Id != exceptId && string.Equals(o.Title, title, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"\"{category.Title}\" already has an option called \"{title}\".");
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

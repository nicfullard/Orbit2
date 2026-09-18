using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Mcp;

/// <summary>
/// The MCP tool surface Claude connects to. Every tool delegates to the same application services the
/// Razor Pages UI uses; the API key's role and department claims drive the same authorization rules.
/// Writes are stamped Source = Api and audited by the services.
/// </summary>
[McpServerToolType]
public sealed class OrbitTools(
    TaskService tasks,
    ProjectService projects,
    CommentService comments,
    AuditService audit,
    UserDirectoryService users,
    DepartmentService departments)
{
    private const string Clear = "none";

    // ---------------------------------------------------------------- tasks

    [McpServerTool(Name = "create_task"), Description(
        "Create a task in Orbit. The task lands directly in the backlog with Source = Api. " +
        "Department: defaults to the project's department when projectId is given, otherwise to departmentId, " +
        "falling back to the API key's own department (a SystemAdmin key may have none, so pass departmentId for standalone tasks). " +
        "A SystemAdmin key may pass a departmentId that differs from the project's to file a cross-department project task: " +
        "the task then belongs to, and is worked by, that department while staying on the project. Other keys are rejected for that.")]
    public Task<string> CreateTask(
        [Description("Task title (required).")] string title,
        [Description("Longer description; markdown is fine.")] string? description = null,
        [Description("Project id (GUID) to file the task under. Optional - tasks can be standalone.")] string? projectId = null,
        [Description("Department id (GUID) the task belongs to. With projectId it defaults to the project's department; a SystemAdmin key may pass another department to file a cross-department project task.")] string? departmentId = null,
        [Description("Low, Medium, High or Critical. Default Medium.")] string? priority = null,
        [Description("Meeting, Planning, Task, Training or Audit - what kind of work it is. Default Task.")] string? type = null,
        [Description("Due date as yyyy-MM-dd.")] string? dueDate = null,
        [Description("Assignee user id (GUID). Must be an active user in the task's department (not necessarily the project's), or a SystemAdmin. Omit to leave unassigned.")] string? assigneeId = null,
        [Description("Optional idempotency key. Retrying with the same key returns the already-created task instead of a duplicate.")] string? idempotencyKey = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var input = new TaskInput
        {
            Title = title,
            Description = description,
            ProjectId = ParseGuid(projectId, "projectId"),
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            Priority = ParseEnum<TaskPriority>(priority, "priority") ?? TaskPriority.Medium,
            Type = ParseEnum<TaskType>(type, "type") ?? TaskType.Task,
            DueDate = ParseDate(dueDate, "dueDate"),
            AssigneeId = ParseGuid(assigneeId, "assigneeId"),
            IdempotencyKey = idempotencyKey
        };
        var task = await tasks.CreateAsync(input, TaskSource.Api, ct);
        return TaskDto(task);
    });

    [McpServerTool(Name = "get_task"), Description("Get a single task's full detail, including its comments and logged time total.")]
    public Task<string> GetTask(
        [Description("Task id (GUID).")] string taskId,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = RequireGuid(taskId, "taskId");
        var task = await tasks.GetAsync(id, ct);
        var taskComments = await comments.ListAsync(id, ct);
        return new
        {
            task = TaskDto(task),
            comments = taskComments.Select(CommentDto).ToList()
        };
    });

    [McpServerTool(Name = "list_tasks"), Description(
        "List tasks with optional filters. Paginated. A Member/DepartmentAdmin key only ever sees its own " +
        "department; a SystemAdmin key can filter by departmentId or omit it for all departments. " +
        "Pass plannedFor = \"today\" to see the team's day plan - the tasks picked in the morning scrum to work on today.")]
    public Task<string> ListTasks(
        [Description("Filter by project id (GUID).")] string? projectId = null,
        [Description("Filter by department id (GUID). SystemAdmin keys only.")] string? departmentId = null,
        [Description("Todo, InProgress, Blocked, Done or Cancelled.")] string? status = null,
        [Description("Filter by assignee user id (GUID).")] string? assigneeId = null,
        [Description("Manual, Api or Recurring - where the task originated.")] string? source = null,
        [Description("Low, Medium, High or Critical.")] string? priority = null,
        [Description("Meeting, Planning, Task, Training or Audit.")] string? type = null,
        [Description("Only tasks due on or before this date (yyyy-MM-dd).")] string? dueBefore = null,
        [Description("Only tasks due on or after this date (yyyy-MM-dd).")] string? dueAfter = null,
        [Description("Filter by sprint id (GUID).")] string? sprintId = null,
        [Description("true = only backlog tasks (no sprint).")] bool? backlogOnly = null,
        [Description("true = exclude Done and Cancelled tasks.")] bool? openOnly = null,
        [Description("Free-text search over title and description.")] string? search = null,
        [Description("Only tasks on the day plan for this date (yyyy-MM-dd), or the literal \"today\".")] string? plannedFor = null,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Page size (1-200). Default 50.")] int pageSize = 50,
        CancellationToken ct = default) => Run(async () =>
    {
        var filter = new TaskFilter
        {
            ProjectId = ParseGuid(projectId, "projectId"),
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            Status = ParseEnum<TaskItemStatus>(status, "status"),
            AssigneeId = ParseGuid(assigneeId, "assigneeId"),
            Source = ParseEnum<TaskSource>(source, "source"),
            Priority = ParseEnum<TaskPriority>(priority, "priority"),
            Type = ParseEnum<TaskType>(type, "type"),
            DueBefore = ParseDate(dueBefore, "dueBefore"),
            DueAfter = ParseDate(dueAfter, "dueAfter"),
            SprintId = ParseGuid(sprintId, "sprintId"),
            BacklogOnly = backlogOnly ?? false,
            OpenOnly = openOnly ?? false,
            PlannedFor = ParsePlanDate(plannedFor, "plannedFor"),
            Search = search,
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 200)
        };
        var result = await tasks.ListAsync(filter, ct);
        return Paged(result, TaskDto);
    });

    [McpServerTool(Name = "update_task"), Description(
        "Update any field of a task. Only the arguments you pass change; omit an argument to leave it as is. " +
        "Pass the literal string \"none\" to clear assigneeId, dueDate, projectId, sprintId or plannedFor (sprintId \"none\" moves the task to the backlog; " +
        "plannedFor \"none\" takes it off the day plan, plannedFor \"today\" puts it on today's plan - closed tasks can't be planned). " +
        "Setting status to Done or Cancelled requires a DepartmentAdmin or SystemAdmin key; a Member key is rejected. " +
        "Member keys can only edit tasks they created or that are assigned to them. " +
        "departmentId moves the task to another department (SystemAdmin keys only); if that differs from the project's department the task " +
        "becomes a cross-department project task. Changing projectId without departmentId moves the task into the new project's department.")]
    public Task<string> UpdateTask(
        [Description("Task id (GUID).")] string taskId,
        [Description("New title.")] string? title = null,
        [Description("New description.")] string? description = null,
        [Description("Todo, InProgress, Blocked, Done or Cancelled.")] string? status = null,
        [Description("Low, Medium, High or Critical.")] string? priority = null,
        [Description("Meeting, Planning, Task, Training or Audit.")] string? type = null,
        [Description("Assignee user id (GUID), or \"none\" to unassign.")] string? assigneeId = null,
        [Description("Due date yyyy-MM-dd, or \"none\" to clear.")] string? dueDate = null,
        [Description("Project id (GUID), or \"none\" to make the task standalone.")] string? projectId = null,
        [Description("Sprint id (GUID) to plan the task into, or \"none\" for the backlog.")] string? sprintId = null,
        [Description("Department id (GUID) to move the task to. SystemAdmin keys only. Omit to keep the task's department (it only follows the project when projectId changes).")] string? departmentId = null,
        [Description("Day-plan date yyyy-MM-dd, \"today\" to put the task on today's plan, or \"none\" to take it off. Anyone in the task's department may plan it; closed tasks can't be planned.")] string? plannedFor = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = RequireGuid(taskId, "taskId");
        var current = await tasks.GetAsync(id, ct);
        var input = new TaskInput
        {
            Title = title ?? current.Title,
            Description = description ?? current.Description,
            ProjectId = IsClear(projectId) ? null : ParseGuid(projectId, "projectId") ?? current.ProjectId,
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            Priority = ParseEnum<TaskPriority>(priority, "priority") ?? current.Priority,
            Type = ParseEnum<TaskType>(type, "type") ?? current.Type,
            AssigneeId = IsClear(assigneeId) ? null : ParseGuid(assigneeId, "assigneeId") ?? current.AssigneeId,
            DueDate = IsClear(dueDate) ? null : ParseDate(dueDate, "dueDate") ?? current.DueDate,
            Status = ParseEnum<TaskItemStatus>(status, "status") ?? current.Status,
            SprintId = IsClear(sprintId) ? null : ParseGuid(sprintId, "sprintId") ?? current.SprintId
        };
        var task = await tasks.UpdateAsync(id, input, ct);
        // The day plan (§6.12) is deliberately not part of TaskInput, so it can't be wiped by a full-state edit.
        if (!string.IsNullOrWhiteSpace(plannedFor))
        {
            await tasks.SetPlannedForAsync(id, IsClear(plannedFor) ? null : ParsePlanDate(plannedFor, "plannedFor"), ct);
            task = await tasks.GetAsync(id, ct);
        }
        return TaskDto(task);
    });

    // ------------------------------------------------------------- comments

    [McpServerTool(Name = "add_comment"), Description("Add a comment to a task, e.g. to explain why you created or changed it. Shown in the UI attributed to Claude.")]
    public Task<string> AddComment(
        [Description("Task id (GUID).")] string taskId,
        [Description("Comment text (markdown is fine).")] string body,
        CancellationToken ct = default) => Run(async () =>
    {
        var comment = await comments.AddAsync(RequireGuid(taskId, "taskId"), body, ct);
        return CommentDto(comment);
    });

    [McpServerTool(Name = "list_comments"), Description("List a task's comments, oldest first.")]
    public Task<string> ListComments(
        [Description("Task id (GUID).")] string taskId,
        CancellationToken ct = default) => Run(async () =>
    {
        var list = await comments.ListAsync(RequireGuid(taskId, "taskId"), ct);
        return new { items = list.Select(CommentDto).ToList(), totalCount = list.Count };
    });

    // ------------------------------------------------------------- projects

    [McpServerTool(Name = "create_project"), Description(
        "Create a project. departmentId defaults to the key's own department (required for a SystemAdmin key). " +
        "ownerId defaults to the Claude agent user; pass a real user's id to make them accountable.")]
    public Task<string> CreateProject(
        [Description("Project name (required).")] string name,
        [Description("Description.")] string? description = null,
        [Description("Department id (GUID).")] string? departmentId = null,
        [Description("Owner user id (GUID). Must belong to the project's department.")] string? ownerId = null,
        [Description("Target completion date yyyy-MM-dd.")] string? targetDate = null,
        [Description("Active, OnHold, Completed or Archived. Default Active.")] string? status = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var input = new ProjectInput
        {
            Name = name,
            Description = description,
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            OwnerId = ParseGuid(ownerId, "ownerId"),
            TargetDate = ParseDate(targetDate, "targetDate"),
            Status = ParseEnum<ProjectStatus>(status, "status") ?? ProjectStatus.Active
        };
        var project = await projects.CreateAsync(input, ct);
        return ProjectDto(project, includeTasks: false);
    });

    [McpServerTool(Name = "get_project"), Description(
        "Get a project's detail including all of its tasks, each with its department. A project can hold tasks for several departments " +
        "(crossDepartment = true). Visible to the project's department, to SystemAdmin keys, and to any department that has tasks filed under it; " +
        "the key's department scoping still applies per task when you go on to get_task / update_task.")]
    public Task<string> GetProject(
        [Description("Project id (GUID).")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var project = await projects.GetAsync(RequireGuid(projectId, "projectId"), ct);
        return ProjectDto(project, includeTasks: true);
    });

    [McpServerTool(Name = "get_project_status"), Description("Lightweight status summary of a project: task counts by status, overdue count, progress and time logged. No task list.")]
    public Task<string> GetProjectStatus(
        [Description("Project id (GUID).")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var s = await projects.GetStatusAsync(RequireGuid(projectId, "projectId"), ct);
        return new
        {
            id = s.Id, name = s.Name, status = s.Status, departmentId = s.DepartmentId, department = s.DepartmentName,
            owner = s.OwnerName, targetDate = s.TargetDate,
            totalTasks = s.Total, openTasks = s.Open, todo = s.Todo, inProgress = s.InProgress, blocked = s.Blocked,
            done = s.Done, cancelled = s.Cancelled, overdue = s.Overdue, percentDone = s.PercentDone,
            totalMinutesLogged = s.TotalMinutesLogged,
            crossDepartment = s.IsCrossDepartment,
            departments = s.ByDepartment.Select(d => new
            {
                departmentId = d.DepartmentId, department = d.Name, totalTasks = d.Total, openTasks = d.Open
            }).ToList()
        };
    });

    [McpServerTool(Name = "list_projects"), Description(
        "List projects with open/total task counts. Same department scoping as list_tasks, plus other departments' projects that have " +
        "tasks filed for the key's department (shared, read-only). Archived projects are excluded unless includeArchived is true.")]
    public Task<string> ListProjects(
        [Description("Filter by department id (GUID). SystemAdmin keys only.")] string? departmentId = null,
        [Description("Active, OnHold, Completed or Archived.")] string? status = null,
        [Description("Include archived projects.")] bool? includeArchived = null,
        [Description("Free-text search over the name.")] string? search = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var list = await projects.ListAsync(new ProjectFilter
        {
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            Status = ParseEnum<ProjectStatus>(status, "status"),
            IncludeArchived = includeArchived ?? false,
            Search = search
        }, ct);
        return new
        {
            items = list.Select(p => new
            {
                id = p.Project.Id, name = p.Project.Name, status = p.Project.Status,
                departmentId = p.Project.DepartmentId, department = p.Project.Department.Name,
                ownerId = p.Project.OwnerId, owner = p.Project.Owner.DisplayName,
                targetDate = p.Project.TargetDate, totalTasks = p.TotalTasks, openTasks = p.OpenTasks,
                doneTasks = p.DoneTasks, percentDone = p.PercentDone
            }).ToList(),
            totalCount = list.Count
        };
    });

    [McpServerTool(Name = "update_project"), Description(
        "Edit a project: name, description, status, owner, target date. Only passed arguments change. " +
        "Member keys can only edit projects owned by the Claude agent user; DepartmentAdmin keys any project in their department.")]
    public Task<string> UpdateProject(
        [Description("Project id (GUID).")] string projectId,
        [Description("New name.")] string? name = null,
        [Description("New description.")] string? description = null,
        [Description("Active, OnHold, Completed or Archived.")] string? status = null,
        [Description("Owner user id (GUID).")] string? ownerId = null,
        [Description("Target date yyyy-MM-dd, or \"none\" to clear.")] string? targetDate = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = RequireGuid(projectId, "projectId");
        var current = await projects.GetAsync(id, ct);
        var input = new ProjectInput
        {
            Name = name ?? current.Name,
            Description = description ?? current.Description,
            Status = ParseEnum<ProjectStatus>(status, "status") ?? current.Status,
            OwnerId = ParseGuid(ownerId, "ownerId") ?? current.OwnerId,
            TargetDate = IsClear(targetDate) ? null : ParseDate(targetDate, "targetDate") ?? current.TargetDate,
            DepartmentId = current.DepartmentId
        };
        var project = await projects.UpdateAsync(id, input, ct);
        return ProjectDto(project, includeTasks: false);
    });

    // ------------------------------------------------------------- lookups

    [McpServerTool(Name = "list_activity"), Description(
        "Read the audit log: every task/project/comment write, who or what made it, and when. " +
        "Defaults to the last 24 hours when from/to are omitted. Scoped to the key's department unless SystemAdmin.")]
    public Task<string> ListActivity(
        [Description("Start of the window, ISO 8601 date-time (UTC assumed if no offset). Default: now - 24h.")] string? from = null,
        [Description("End of the window, ISO 8601 date-time. Default: now.")] string? to = null,
        [Description("Task, Project, Sprint, RecurringTaskDefinition, Department, User or ApiKey.")] string? entityType = null,
        [Description("Scope to one entity id (GUID), e.g. a task or project.")] string? entityId = null,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Page size (1-200). Default 100.")] int pageSize = 100,
        CancellationToken ct = default) => Run(async () =>
    {
        var result = await audit.ListAsync(new AuditFilter
        {
            From = ParseDateTime(from, "from"),
            To = ParseDateTime(to, "to"),
            EntityType = string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim(),
            EntityId = ParseGuid(entityId, "entityId"),
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 200)
        }, ct);
        return Paged(result, a => new
        {
            id = a.Id, timestamp = a.Timestamp, entityType = a.EntityType, entityId = a.EntityId, action = a.Action,
            actorType = a.ActorType, actorId = a.ActorId, actor = a.ActorName, departmentId = a.DepartmentId,
            summary = a.Summary, details = JsonDocument.Parse(a.Details).RootElement.Clone()
        });
    });

    [McpServerTool(Name = "list_users"), Description(
        "List users (id, displayName, email, role, departmentId). Optional name/email fragment filter, e.g. \"Bob\". " +
        "A Member/DepartmentAdmin key sees only its own department's users; a SystemAdmin key can pass departmentId or omit it for everyone.")]
    public Task<string> ListUsers(
        [Description("Name or email fragment to match (case-insensitive).")] string? query = null,
        [Description("Filter by department id (GUID). SystemAdmin keys only.")] string? departmentId = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var list = await users.ListAsync(query, ParseGuid(departmentId, "departmentId"), includeInactive: false, ct);
        return new
        {
            items = list.Select(u => new
            {
                id = u.Id, displayName = u.DisplayName, email = u.Email, role = u.Role,
                departmentId = u.DepartmentId, department = u.DepartmentName
            }).ToList(),
            totalCount = list.Count
        };
    });

    [McpServerTool(Name = "list_departments"), Description("List all (non-archived) departments: id, name, description. Available to every role.")]
    public Task<string> ListDepartments(CancellationToken ct = default) => Run(async () =>
    {
        var list = await departments.ListAsync(includeArchived: false, ct);
        return new
        {
            items = list.Select(d => new { id = d.Id, name = d.Name, description = d.Description }).ToList(),
            totalCount = list.Count
        };
    });

    // ------------------------------------------------------------- plumbing

    private static async Task<string> Run<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return JsonSerializer.Serialize(result, OrbitJson.Options);
        }
        catch (OrbitException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static object Paged<T>(PagedResult<T> result, Func<T, object> map) => new
    {
        items = result.Items.Select(map).ToList(),
        page = result.Page,
        pageSize = result.PageSize,
        totalCount = result.TotalCount,
        totalPages = result.TotalPages
    };

    private static object TaskDto(TaskItem t) => new
    {
        id = t.Id,
        title = t.Title,
        description = t.Description,
        status = t.Status,
        priority = t.Priority,
        type = t.Type,
        source = t.Source,
        departmentId = t.DepartmentId,
        department = t.Department?.Name,
        projectId = t.ProjectId,
        project = t.Project?.Name,
        assigneeId = t.AssigneeId,
        assignee = t.Assignee?.DisplayName,
        createdById = t.CreatedById,
        createdBy = t.CreatedBy is null ? null : t.CreatedBy.IsSystemAccount ? "Claude" : t.CreatedBy.DisplayName,
        dueDate = t.DueDate,
        plannedFor = t.PlannedFor,
        sprintId = t.SprintId,
        sprint = t.Sprint?.Name,
        inBacklog = t.SprintId is null,
        recurringTaskDefinitionId = t.RecurringTaskDefinitionId,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt,
        completedAt = t.CompletedAt,
        firstRespondedAt = t.FirstRespondedAt
    };

    private static object CommentDto(Comment c) => new
    {
        id = c.Id,
        taskId = c.TaskId,
        authorId = c.AuthorId,
        author = c.Author is null ? "Claude" : c.Author.IsSystemAccount ? "Claude" : c.Author.DisplayName,
        body = c.Body,
        createdAt = c.CreatedAt
    };

    private static object ProjectDto(Project p, bool includeTasks)
    {
        var all = p.Tasks ?? [];
        return new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            status = p.Status,
            departmentId = p.DepartmentId,
            department = p.Department?.Name,
            ownerId = p.OwnerId,
            owner = p.Owner?.DisplayName,
            targetDate = p.TargetDate,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
            totalTasks = all.Count,
            openTasks = all.Count(t => !t.Status.IsClosed()),
            doneTasks = all.Count(t => t.Status == TaskItemStatus.Done),
            crossDepartment = all.Any(t => t.DepartmentId != p.DepartmentId),
            tasks = includeTasks
                ? all.OrderBy(t => t.Status.IsClosed()).ThenBy(t => t.DueDate).Select(t => new
                {
                    id = t.Id, title = t.Title, status = t.Status, priority = t.Priority, type = t.Type, source = t.Source,
                    departmentId = t.DepartmentId, department = t.Department?.Name,
                    assigneeId = t.AssigneeId, assignee = t.Assignee?.DisplayName, dueDate = t.DueDate,
                    sprintId = t.SprintId, updatedAt = t.UpdatedAt
                }).ToList()
                : null
        };
    }

    private static bool IsClear(string? value) => string.Equals(value?.Trim(), Clear, StringComparison.OrdinalIgnoreCase);

    private static Guid RequireGuid(string? value, string name) =>
        ParseGuid(value, name) ?? throw new McpException($"{name} is required.");

    private static Guid? ParseGuid(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value.Trim(), out var g) ? g : throw new McpException($"{name} must be a GUID; got \"{value}\".");
    }

    private static T? ParseEnum<T>(string? value, string name) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
        return Enum.TryParse<T>(text, ignoreCase: true, out var result) && Enum.IsDefined(result)
            ? result
            : throw new McpException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return DateOnly.FromDateTime(dt);
        throw new McpException($"{name} must be a date like 2026-09-30; got \"{value}\".");
    }

    /// <summary>A day-plan date: the literal "today" (UTC date, like every other date in Orbit) or yyyy-MM-dd.</summary>
    private static DateOnly? ParsePlanDate(string? value, string name) =>
        string.Equals(value?.Trim(), "today", StringComparison.OrdinalIgnoreCase)
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : ParseDate(value, name);

    private static DateTime? ParseDateTime(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        throw new McpException($"{name} must be an ISO 8601 date-time; got \"{value}\".");
    }
}

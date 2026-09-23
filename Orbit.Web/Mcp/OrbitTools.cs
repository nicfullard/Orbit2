using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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
    NumberingService numbering,
    TaskService tasks,
    ProjectService projects,
    CommentService comments,
    AuditService audit,
    UserDirectoryService users,
    DepartmentService departments,
    TaskStructureService structure,
    CriticalPathService criticalPaths,
    AttachmentService attachments,
    IOptions<AttachmentOptions> attachmentOptions)
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
        [Description("Planned start date as yyyy-MM-dd (not after the due date).")] string? startDate = null,
        [Description("Parent task id (GUID) to create this as a subtask. The parent must be on the same project (or, for a standalone task, be a standalone task in the same department) and still open.")] string? parentTaskId = null,
        [Description("Estimated effort in minutes, e.g. 90 for an hour and a half. Omit or 0 for no estimate.")] int? estimateMinutes = null,
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
            StartDate = ParseDate(startDate, "startDate"),
            DueDate = ParseDate(dueDate, "dueDate"),
            AssigneeId = ParseGuid(assigneeId, "assigneeId"),
            ParentTaskId = ParseGuid(parentTaskId, "parentTaskId"),
            EstimateMinutes = estimateMinutes,
            IdempotencyKey = idempotencyKey
        };
        var task = await tasks.CreateAsync(input, TaskSource.Api, ct);
        return TaskDto(task);
    });

    [McpServerTool(Name = "get_task"), Description(
        "Get a single task's full detail: its fields, parent chain (ancestors), subtasks, the dependencies it waits on (predecessors) and the ones " +
        "waiting on it (successors) - each with type FS/SS/FF/SF, lag, whether the gate is met and any planned-date conflict - plus waitingOn: " +
        "the unmet links (own or inherited from a parent) that currently block its next status move. Also its comments and attachments " +
        "(file name, size, uploader and a downloadPath relative to Orbit's base URL; get_attachment returns a file's content; files are uploaded in the web UI).")]
    public Task<string> GetTask(
        [Description("Task id (GUID), or task number such as T-26-00012.")] string taskId,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = await TaskIdAsync(taskId, ct);
        var task = await tasks.GetAsync(id, ct);
        var taskComments = await comments.ListAsync(id, ct);
        var taskFiles = await attachments.ListForTaskAsync(id, ct);
        var s = await structure.GetStructureAsync(task, ct);
        return new
        {
            task = TaskDto(task),
            ancestors = s.Ancestors.Select(a => new { id = a.Id, title = a.Title, status = a.Status, departmentId = a.DepartmentId, department = a.Department?.Name }).ToList(),
            subtasks = s.Children.Select(c => new
            {
                id = c.Id, title = c.Title, status = c.Status, priority = c.Priority,
                departmentId = c.DepartmentId, department = c.Department?.Name,
                assigneeId = c.AssigneeId, assignee = c.Assignee?.DisplayName, startDate = c.StartDate, dueDate = c.DueDate
            }).ToList(),
            subtasksClosed = s.ChildrenClosed,
            predecessors = s.Predecessors.Select(LinkDto).ToList(),
            successors = s.Successors.Select(LinkDto).ToList(),
            waitingOn = s.WaitingOn.Select(w => new
            {
                dependencyId = w.Link.Id, taskId = w.Predecessor.Id, title = w.Predecessor.Title, status = w.Predecessor.Status,
                type = w.Link.Type, code = w.Link.Type.Code(), viaParentId = w.ViaAncestor?.Id, viaParent = w.ViaAncestor?.Title, reason = w.Describe()
            }).ToList(),
            comments = taskComments.Select(CommentDto).ToList(),
            attachments = taskFiles.Select(AttachmentDto).ToList()
        };
    });

    [McpServerTool(Name = "list_tasks"), Description(
        "List tasks with optional filters. Paginated. A Member/DepartmentAdmin key only ever sees its own " +
        "department; a SystemAdmin key can filter by departmentId or omit it for all departments. " +
        "Pass plannedFor = \"today\" to see the team's day plan - the tasks picked in the morning scrum to work on today.")]
    public Task<string> ListTasks(
        [Description("Filter by project id (GUID).")] string? projectId = null,
        [Description("Filter by department id (GUID). SystemAdmin keys only.")] string? departmentId = null,
        [Description("Todo, InProgress, Waiting, Blocked, Done or Cancelled.")] string? status = null,
        [Description("Filter by assignee user id (GUID).")] string? assigneeId = null,
        [Description("true = only tasks with no assignee - work nobody has picked up yet. Overrides assigneeId.")] bool? unassigned = null,
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
        [Description("Only the direct subtasks of this task id (GUID).")] string? parentTaskId = null,
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
            Unassigned = unassigned ?? false,
            Source = ParseEnum<TaskSource>(source, "source"),
            Priority = ParseEnum<TaskPriority>(priority, "priority"),
            Type = ParseEnum<TaskType>(type, "type"),
            DueBefore = ParseDate(dueBefore, "dueBefore"),
            DueAfter = ParseDate(dueAfter, "dueAfter"),
            SprintId = ParseGuid(sprintId, "sprintId"),
            BacklogOnly = backlogOnly ?? false,
            OpenOnly = openOnly ?? false,
            PlannedFor = ParsePlanDate(plannedFor, "plannedFor"),
            ParentTaskId = ParseGuid(parentTaskId, "parentTaskId"),
            Search = search,
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 200)
        };
        var result = await tasks.ListAsync(filter, ct);
        return Paged(result, TaskDto);
    });

    [McpServerTool(Name = "update_task"), Description(
        "Update any field of a task. Only the arguments you pass change; omit an argument to leave it as is. " +
        "Pass the literal string \"none\" to clear assigneeId, dueDate, startDate, parentTaskId, estimateMinutes, projectId, sprintId or plannedFor (sprintId \"none\" moves the task to the backlog; " +
        "plannedFor \"none\" takes it off the day plan, plannedFor \"today\" puts it on today's plan - closed tasks can't be planned). " +
        "Setting status to Done or Cancelled requires a DepartmentAdmin or SystemAdmin key; a Member key is rejected. " +
        "Member keys can only edit tasks they created or that are assigned to them. " +
        "departmentId moves the task to another department (SystemAdmin keys only); if that differs from the project's department the task " +
        "becomes a cross-department project task. Changing projectId without departmentId moves the task into the new project's department.")]
    public Task<string> UpdateTask(
        [Description("Task id (GUID), or task number such as T-26-00012.")] string taskId,
        [Description("New title.")] string? title = null,
        [Description("New description.")] string? description = null,
        [Description("Todo, InProgress, Waiting, Blocked, Done or Cancelled.")] string? status = null,
        [Description("Low, Medium, High or Critical.")] string? priority = null,
        [Description("Meeting, Planning, Task, Training or Audit.")] string? type = null,
        [Description("Assignee user id (GUID), or \"none\" to unassign.")] string? assigneeId = null,
        [Description("Due date yyyy-MM-dd, or \"none\" to clear.")] string? dueDate = null,
        [Description("Project id (GUID), or \"none\" to make the task standalone.")] string? projectId = null,
        [Description("Sprint id (GUID) to plan the task into, or \"none\" for the backlog.")] string? sprintId = null,
        [Description("Department id (GUID) to move the task to. SystemAdmin keys only. Omit to keep the task's department (it only follows the project when projectId changes).")] string? departmentId = null,
        [Description("Day-plan date yyyy-MM-dd, \"today\" to put the task on today's plan, or \"none\" to take it off. Anyone in the task's department may plan it; closed tasks can't be planned.")] string? plannedFor = null,
        [Description("Planned start date yyyy-MM-dd (not after the due date), or \"none\" to clear.")] string? startDate = null,
        [Description("Parent task id (GUID) to make this a subtask (same project, or same department for standalone tasks), or \"none\" to detach it. A status change gated by a dependency, or closing a parent with open subtasks, is rejected with the reason.")] string? parentTaskId = null,
        [Description("Estimated effort in minutes (e.g. 90), or \"none\" to clear the estimate.")] string? estimateMinutes = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = await TaskIdAsync(taskId, ct);
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
            StartDate = IsClear(startDate) ? null : ParseDate(startDate, "startDate") ?? current.StartDate,
            DueDate = IsClear(dueDate) ? null : ParseDate(dueDate, "dueDate") ?? current.DueDate,
            ParentTaskId = IsClear(parentTaskId) ? null : ParseGuid(parentTaskId, "parentTaskId") ?? current.ParentTaskId,
            EstimateMinutes = IsClear(estimateMinutes) ? null : ParseInt(estimateMinutes, "estimateMinutes") ?? current.EstimateMinutes,
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
        [Description("Task id (GUID), or task number such as T-26-00012.")] string taskId,
        [Description("Comment text (markdown is fine).")] string body,
        CancellationToken ct = default) => Run(async () =>
    {
        var comment = await comments.AddAsync(await TaskIdAsync(taskId, ct), body, ct);
        return CommentDto(comment);
    });

    [McpServerTool(Name = "list_comments"), Description("List a task's comments, oldest first.")]
    public Task<string> ListComments(
        [Description("Task id (GUID), or task number such as T-26-00012.")] string taskId,
        CancellationToken ct = default) => Run(async () =>
    {
        var list = await comments.ListAsync(await TaskIdAsync(taskId, ct), ct);
        return new { items = list.Select(CommentDto).ToList(), totalCount = list.Count };
    });

    // ---------------------------------------------------------------- attachments

    [McpServerTool(Name = "get_attachment"), Description(
        "Get the content of a file attached to a task or project - one of the attachments that get_task / get_project list. " +
        "Returns two content blocks: JSON describing the file (its metadata, taskId or projectId, and what follows), then the file itself: " +
        "text files (text/*, JSON, XML and the like, or an untyped file that is valid UTF-8) as text, PNG/JPEG/GIF/WebP as an image, " +
        "anything else (PDF, Office documents, archives...) as an embedded resource carrying base64 data. A file over Orbit's size limit for " +
        "this tool is described but not returned: the JSON says so, and downloadPath is the web UI download. Visible to whoever may see the task or project.")]
    public Task<CallToolResult> GetAttachment(
        [Description("Attachment id (GUID), from the attachments list of get_task or get_project.")] string attachmentId,
        CancellationToken ct = default) => RunResult(async () =>
    {
        var (attachment, content) = await attachments.DownloadAsync(RequireGuid(attachmentId, "attachmentId"), ct);
        var limits = attachmentOptions.Value;
        if (content.Length > limits.MaxMcpFileSizeBytes)
        {
            return new CallToolResult
            {
                Content =
                [
                    Json(new
                    {
                        attachment = AttachmentDto(attachment), taskId = attachment.TaskId, projectId = attachment.ProjectId,
                        content = "notReturned",
                        reason = $"The file is {attachment.SizeBytes} bytes, over the {limits.MaxMcpFileSizeMb} MB limit for get_attachment; it can be downloaded from downloadPath in the web UI."
                    })
                ]
            };
        }

        var kind = AttachmentClassifier.Classify(attachment.ContentType, content);
        ContentBlock body = kind switch
        {
            AttachmentContentKind.Text => new TextContentBlock { Text = AttachmentClassifier.DecodeText(content) },
            AttachmentContentKind.Image => new ImageContentBlock
            {
                Data = content,
                MimeType = AttachmentClassifier.MediaType(attachment.ContentType)
            },
            _ => new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = $"orbit://attachments/{attachment.Id}",
                    MimeType = attachment.ContentType,
                    Blob = content
                }
            }
        };
        return new CallToolResult
        {
            Content =
            [
                Json(new
                {
                    attachment = AttachmentDto(attachment), taskId = attachment.TaskId, projectId = attachment.ProjectId,
                    content = kind switch { AttachmentContentKind.Text => "text", AttachmentContentKind.Image => "image", _ => "binary" }
                }),
                body
            ]
        };
    });

    // --------------------------------------------------------- dependencies

    [McpServerTool(Name = "add_dependency"), Description(
        "Link two tasks: the successor waits on the predecessor. type: FS (default - the successor can't start until the predecessor finishes), " +
        "SS (can't start until it starts), FF (can't finish until it finishes), SF (can't finish until it starts). Start = leaving Todo, finish = Done; " +
        "a Cancelled predecessor releases its successors. lagDays is calendar days between the two ends (negative = lead) and only affects the " +
        "planned-date check, never the workflow gate. Both tasks must be on the same project - a standalone task can't be linked; the key needs " +
        "edit rights on the successor. Self-links, duplicates, links between a task and its own parent/subtask, and cycles are rejected with the reason.")]
    public Task<string> AddDependency(
        [Description("The task that must start/finish first (GUID, or task number).")] string predecessorTaskId,
        [Description("The task that waits (GUID, or task number).")] string successorTaskId,
        [Description("FS, SS, FF or SF (FinishToStart etc. also accepted). Default FS.")] string? type = null,
        [Description("Lag in calendar days; negative is a lead. Default 0.")] int lagDays = 0,
        CancellationToken ct = default) => Run(async () =>
    {
        var link = await structure.AddAsync(new DependencyInput
        {
            PredecessorTaskId = await TaskIdAsync(predecessorTaskId, ct, "predecessorTaskId"),
            SuccessorTaskId = await TaskIdAsync(successorTaskId, ct, "successorTaskId"),
            Type = ParseDependencyType(type),
            LagDays = lagDays
        }, ct);
        return new
        {
            id = link.Id, predecessorId = link.PredecessorTaskId, predecessor = link.Predecessor.Title,
            successorId = link.SuccessorTaskId, successor = link.Successor.Title,
            type = link.Type, code = link.Type.Code(), lagDays = link.LagDays, createdAt = link.CreatedAt
        };
    });

    [McpServerTool(Name = "remove_dependency"), Description(
        "Remove a dependency link by its id (from get_task or add_dependency), or by the predecessorTaskId + successorTaskId pair. " +
        "Same rights as add_dependency: the key must be able to edit the successor.")]
    public Task<string> RemoveDependency(
        [Description("The dependency id (GUID). Omit when passing the pair.")] string? dependencyId = null,
        [Description("Predecessor task id (GUID), together with successorTaskId.")] string? predecessorTaskId = null,
        [Description("Successor task id (GUID), together with predecessorTaskId.")] string? successorTaskId = null,
        CancellationToken ct = default) => Run(async () =>
    {
        if (ParseGuid(dependencyId, "dependencyId") is Guid id)
            await structure.RemoveAsync(id, ct);
        else
            await structure.RemoveAsync(await TaskIdAsync(predecessorTaskId, ct, "predecessorTaskId"), await TaskIdAsync(successorTaskId, ct, "successorTaskId"), ct);
        return new { removed = true };
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
        [Description("Required project buffer in working days - schedule protection kept before the target date (spec §6.17). Omit or 0 for none.")] int? requiredBufferWorkingDays = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var input = new ProjectInput
        {
            Name = name,
            Description = description,
            DepartmentId = ParseGuid(departmentId, "departmentId"),
            OwnerId = ParseGuid(ownerId, "ownerId"),
            TargetDate = ParseDate(targetDate, "targetDate"),
            Status = ParseEnum<ProjectStatus>(status, "status") ?? ProjectStatus.Active,
            RequiredBufferWorkingDays = requiredBufferWorkingDays
        };
        var project = await projects.CreateAsync(input, ct);
        return ProjectDto(project, includeTasks: false);
    });

    [McpServerTool(Name = "get_project"), Description(
        "Get a project's detail including all of its tasks, each with its department. A project can hold tasks for several departments " +
        "(crossDepartment = true). Visible to the project's department, to SystemAdmin keys, and to any department that has tasks filed under it; " +
        "the key's department scoping still applies per task when you go on to get_task / update_task.")]
    public Task<string> GetProject(
        [Description("Project id (GUID), or project number such as P-26-00003.")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var project = await projects.GetAsync(await ProjectIdAsync(projectId, ct), ct);
        var links = await structure.ListForProjectAsync(project.Id, ct);
        var files = await attachments.ListForProjectAsync(project.Id, ct);
        return ProjectDto(project, includeTasks: true, links, files);
    });

    [McpServerTool(Name = "get_project_status"), Description("Lightweight status summary of a project: task counts by status, overdue count, progress, time logged, and the headline of its last critical path analysis (criticalPath, null if never run; see get_critical_path). No task list.")]
    public Task<string> GetProjectStatus(
        [Description("Project id (GUID), or project number such as P-26-00003.")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var s = await projects.GetStatusAsync(await ProjectIdAsync(projectId, ct), ct);
        var cp = await criticalPaths.GetSummaryAsync(s.Id, ct);
        return new
        {
            id = s.Id, name = s.Name, status = s.Status, departmentId = s.DepartmentId, department = s.DepartmentName,
            owner = s.OwnerName, targetDate = s.TargetDate, requiredBufferWorkingDays = s.RequiredBufferWorkingDays,
            criticalPath = cp is null ? null : new
            {
                analysisId = cp.AnalysisId, runAt = cp.RunAt, isStale = cp.IsStale, plannedCompletion = cp.PlannedCompletion, targetDate = cp.TargetDate,
                bufferStatus = cp.BufferStatus, bufferRemainingDays = cp.BufferRemainingDays, bufferConsumptionPercent = cp.BufferConsumptionPercent,
                criticalTasks = cp.CriticalTaskCount, nearCriticalTasks = cp.NearCriticalTaskCount, warnings = cp.WarningCount
            },
            totalTasks = s.Total, openTasks = s.Open, todo = s.Todo, inProgress = s.InProgress, waiting = s.Waiting, blocked = s.Blocked,
            done = s.Done, cancelled = s.Cancelled, overdue = s.Overdue, percentDone = s.PercentDone,
            totalMinutesLogged = s.TotalMinutesLogged,
            totalMinutesEstimated = s.TotalMinutesEstimated,
            crossDepartment = s.IsCrossDepartment,
            departments = s.ByDepartment.Select(d => new
            {
                departmentId = d.DepartmentId, department = d.Name, totalTasks = d.Total, openTasks = d.Open,
                todo = d.Todo, inProgress = d.InProgress, waiting = d.Waiting, blocked = d.Blocked, done = d.Done, cancelled = d.Cancelled,
                overdue = d.Overdue, percentDone = d.PercentDone
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
        "Edit a project: name, description, status, owner, target date, required project buffer. Only passed arguments change. " +
        "Member keys can only edit projects owned by the Claude agent user; DepartmentAdmin keys any project in their department.")]
    public Task<string> UpdateProject(
        [Description("Project id (GUID), or project number such as P-26-00003.")] string projectId,
        [Description("New name.")] string? name = null,
        [Description("New description.")] string? description = null,
        [Description("Active, OnHold, Completed or Archived.")] string? status = null,
        [Description("Owner user id (GUID).")] string? ownerId = null,
        [Description("Target date yyyy-MM-dd, or \"none\" to clear.")] string? targetDate = null,
        [Description("Required project buffer in working days, or \"none\" to clear.")] string? requiredBufferWorkingDays = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var id = await ProjectIdAsync(projectId, ct);
        var current = await projects.GetAsync(id, ct);
        var input = new ProjectInput
        {
            Name = name ?? current.Name,
            Description = description ?? current.Description,
            Status = ParseEnum<ProjectStatus>(status, "status") ?? current.Status,
            OwnerId = ParseGuid(ownerId, "ownerId") ?? current.OwnerId,
            TargetDate = IsClear(targetDate) ? null : ParseDate(targetDate, "targetDate") ?? current.TargetDate,
            RequiredBufferWorkingDays = IsClear(requiredBufferWorkingDays) ? null : ParseInt(requiredBufferWorkingDays, "requiredBufferWorkingDays") ?? current.RequiredBufferWorkingDays,
            DepartmentId = current.DepartmentId
        };
        var project = await projects.UpdateAsync(id, input, ct);
        return ProjectDto(project, includeTasks: false);
    });

    // ------------------------------------------------------- critical path

    [McpServerTool(Name = "get_critical_path"), Description(
        "The project's most recent critical path analysis (spec §6.17), as Orbit calculated and stored it: planned completion against the target date, " +
        "project buffer consumed/remaining with its Green/Amber/Red status, the critical paths, every analysed task with its early/late dates and " +
        "total/free float in working days, plan-readiness warnings, and early-completion / recovery opportunities. isStale is true when the schedule " +
        "has changed since the run. Use this rather than working criticality out from raw task data. When nothing has been run yet analysisId is null: " +
        "call run_critical_path_analysis.")]
    public Task<string> GetCriticalPath(
        [Description("Project id (GUID), or project number such as P-26-00003.")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var view = await criticalPaths.GetLatestAsync(await ProjectIdAsync(projectId, ct), ct);
        if (view is null)
            return (object)new { analysisId = (Guid?)null, isStale = true, message = "No critical path analysis has been run for this project yet. Call run_critical_path_analysis to run one." };
        return AnalysisDto(view.Analysis.Id, view.IsStale, view.Result);
    });

    [McpServerTool(Name = "run_critical_path_analysis"), Description(
        "Run a fresh critical path analysis on a project's current plan and store it as the project's current analysis - a deliberate " +
        "project-management action, never automatic; needs edit rights on the project. Returns the same shape as get_critical_path. If plan " +
        "readiness blocks the run (a circular dependency, nothing scheduled) it returns blocked = true with the errors and stores nothing. " +
        "Nothing is rescheduled: Orbit identifies scheduling conditions, the project manager changes the plan.")]
    public Task<string> RunCriticalPathAnalysis(
        [Description("Project id (GUID), or project number such as P-26-00003.")] string projectId,
        CancellationToken ct = default) => Run(async () =>
    {
        var result = await criticalPaths.RunAsync(await ProjectIdAsync(projectId, ct), ct);
        if (result.Blocked)
            return (object)new { blocked = true, errors = result.Errors.Select(IssueDto).ToList(), warnings = result.Warnings.Select(IssueDto).ToList() };
        var view = await criticalPaths.GetLatestAsync(result.ProjectId, ct);
        return AnalysisDto(view?.Analysis.Id, view?.IsStale ?? false, result);
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

    /// <summary>For a tool that builds its own content blocks (get_attachment); errors surface the same way as in Run.</summary>
    private static async Task<CallToolResult> RunResult(Func<Task<CallToolResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OrbitException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>A JSON text block, serialized the same way as every other tool's result.</summary>
    private static TextContentBlock Json(object value) => new() { Text = JsonSerializer.Serialize(value, OrbitJson.Options) };

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
        number = t.Number,
        title = t.Title,
        description = t.Description,
        status = t.Status,
        priority = t.Priority,
        type = t.Type,
        estimateMinutes = t.EstimateMinutes,
        source = t.Source,
        departmentId = t.DepartmentId,
        department = t.Department?.Name,
        projectId = t.ProjectId,
        project = t.Project?.Name,
        assigneeId = t.AssigneeId,
        assignee = t.Assignee?.DisplayName,
        createdById = t.CreatedById,
        createdBy = t.CreatedBy is null ? null : t.CreatedBy.IsSystemAccount ? "Claude" : t.CreatedBy.DisplayName,
        parentTaskId = t.ParentTaskId,
        parentTask = t.ParentTask?.Title,
        startDate = t.StartDate,
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

    private static object ProjectDto(Project p, bool includeTasks, IReadOnlyList<TaskDependency>? dependencies = null, IReadOnlyList<Attachment>? attachments = null)
    {
        var all = p.Tasks ?? [];
        return new
        {
            id = p.Id,
            number = p.Number,
            name = p.Name,
            description = p.Description,
            status = p.Status,
            departmentId = p.DepartmentId,
            department = p.Department?.Name,
            ownerId = p.OwnerId,
            owner = p.Owner?.DisplayName,
            targetDate = p.TargetDate,
            requiredBufferWorkingDays = p.RequiredBufferWorkingDays,
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
                    assigneeId = t.AssigneeId, assignee = t.Assignee?.DisplayName,
                    parentTaskId = t.ParentTaskId, startDate = t.StartDate, dueDate = t.DueDate,
                    sprintId = t.SprintId, updatedAt = t.UpdatedAt
                }).ToList()
                : null,
            // Every link on the project (§6.15) - with parentTaskId on each task, enough to rebuild the tree and the graph a Gantt draws.
            dependencies = dependencies?.Select(l => new
            {
                id = l.Id, predecessorId = l.PredecessorTaskId, predecessor = l.Predecessor?.Title,
                successorId = l.SuccessorTaskId, successor = l.Successor?.Title,
                type = l.Type, code = l.Type.Code(), lagDays = l.LagDays
            }).ToList(),
            // Files attached to the project itself (§6.18); each task's own files come with get_task.
            attachments = attachments?.Select(AttachmentDto).ToList()
        };
    }

    /// <summary>An attachment (§6.18). downloadPath is relative to Orbit's base URL; the bytes aren't returned over MCP.</summary>
    private static object AttachmentDto(Attachment a) => new
    {
        id = a.Id, fileName = a.FileName, contentType = a.ContentType, sizeBytes = a.SizeBytes,
        uploadedById = a.UploadedById, uploadedBy = a.UploadedBy?.DisplayName, uploadedAt = a.UploadedAt,
        downloadPath = $"/Attachments/Download/{a.Id}"
    };

    private static object IssueDto(PlanIssue i) => new { code = i.Code, message = i.Message, informational = i.Informational, taskIds = i.TaskIds, linkIds = i.LinkIds };

    /// <summary>A stored (or just-run) critical path analysis (§6.17), headline first, then paths, tasks with float, warnings and opportunities.</summary>
    private static object AnalysisDto(Guid? analysisId, bool isStale, CriticalPathResult r)
    {
        var s = r.Schedule;
        return new
        {
            analysisId, runAt = r.RunAt, isStale, blocked = r.Blocked,
            plannedCompletion = s.PlannedCompletion, networkCompletion = s.NetworkCompletion, targetDate = s.TargetDate, internalCompletion = s.InternalCompletion,
            requiredBuffer = s.RequiredBufferDays, bufferConsumed = s.BufferConsumedDays, bufferRemaining = s.BufferRemainingDays,
            bufferConsumptionPercent = s.BufferConsumptionPercent, headroomDays = s.HeadroomDays, daysBeyondTarget = s.DaysBeyondTarget,
            bufferStatus = s.BufferStatus, note = s.Note,
            criticalTaskCount = r.CriticalTaskCount, nearCriticalTaskCount = r.NearCriticalTaskCount,
            criticalPathCount = r.CriticalPaths.Count, criticalPathsOmitted = r.CriticalPathsOmitted,
            criticalPaths = r.CriticalPaths.Select(p => new { taskIds = p.TaskIds, titles = p.Titles, start = p.Start, end = p.End }).ToList(),
            criticalTasks = r.Tasks.Where(t => t.IsCritical).Select(TaskAnalysisDto).ToList(),
            nearCriticalTasks = r.Tasks.Where(t => t.IsNearCritical).Select(TaskAnalysisDto).ToList(),
            tasks = r.Tasks.Select(TaskAnalysisDto).ToList(),
            drivingLinkIds = r.DrivingLinkIds,
            opportunities = r.Opportunities,
            earlyCompletions = r.EarlyCompletions,
            warnings = r.Warnings.Select(IssueDto).ToList(),
            errors = r.Errors.Select(IssueDto).ToList(),
            thresholds = r.Thresholds
        };
    }

    private static object TaskAnalysisDto(TaskAnalysis t) => new
    {
        taskId = t.TaskId, title = t.Title, status = t.Status, assignee = t.Assignee, plannedStart = t.PlannedStart, plannedDue = t.PlannedDue,
        earlyStart = t.EarlyStart, earlyFinish = t.EarlyFinish, lateStart = t.LateStart, lateFinish = t.LateFinish,
        totalFloat = t.TotalFloat, freeFloat = t.FreeFloat, spanWorkingDays = t.SpanWorkingDays,
        inNetwork = t.InNetwork, isCritical = t.IsCritical, isNearCritical = t.IsNearCritical
    };

    private static bool IsClear(string? value) => string.Equals(value?.Trim(), Clear, StringComparison.OrdinalIgnoreCase);

    /// <summary>A dependency link as seen from one task: the task at the far end, the type, lag, and whether the gate is met.</summary>
    private static object LinkDto(DependencyView v) => new
    {
        dependencyId = v.Link.Id,
        taskId = v.Other.Id, title = v.Other.Title, status = v.Other.Status,
        departmentId = v.Other.DepartmentId, department = v.Other.Department?.Name,
        startDate = v.Other.StartDate, dueDate = v.Other.DueDate,
        type = v.Link.Type, code = v.Link.Type.Code(), lagDays = v.Link.LagDays,
        met = v.Met, dateConflict = v.DateConflict
    };

    /// <summary>FS / SS / FF / SF, or the enum names; default FS.</summary>
    private static DependencyType ParseDependencyType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DependencyType.FinishToStart;
        return value.Trim().ToUpperInvariant() switch
        {
            "FS" => DependencyType.FinishToStart,
            "SS" => DependencyType.StartToStart,
            "FF" => DependencyType.FinishToFinish,
            "SF" => DependencyType.StartToFinish,
            _ => ParseEnum<DependencyType>(value, "type") ?? DependencyType.FinishToStart
        };
    }

    private static Guid RequireGuid(string? value, string name) =>
        ParseGuid(value, name) ?? throw new McpException($"{name} is required.");

    /// <summary>A required task argument: its GUID, or its number (T-26-00012, §5.1).</summary>
    private async Task<Guid> TaskIdAsync(string? value, CancellationToken ct, string name = "taskId") =>
        NumberingService.IsNumber(value)
            ? await numbering.FindTaskIdAsync(value!, ct) ?? throw new McpException($"No task is numbered {NumberingService.Normalise(value!)}.")
            : RequireGuid(value, name);

    /// <summary>A required project argument: its GUID, or its number (P-26-00003, §5.1).</summary>
    private async Task<Guid> ProjectIdAsync(string? value, CancellationToken ct) =>
        NumberingService.IsNumber(value)
            ? await numbering.FindProjectIdAsync(value!, ct) ?? throw new McpException($"No project is numbered {NumberingService.Normalise(value!)}.")
            : RequireGuid(value, "projectId");

    private static int? ParseInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new McpException($"{name} must be a whole number; got \"{value}\".");
    }

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

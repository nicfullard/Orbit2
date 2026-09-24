using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Subtasks and dependencies (spec §6.15): the parent/child tree, the dependency links, the workflow gates they put on
/// status changes and the planned-date check. <see cref="TaskService"/> calls in here for every rule; the pages and the
/// MCP tools call in for what to show. Tree and cycle checks load a task's whole <em>scope</em> - its project, or its
/// department's standalone tasks - once and walk it in memory, which is fine at hundreds of tasks (§9).
/// </summary>
public sealed class TaskStructureService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    private const int MaxDepth = 64;

    // ------------------------------------------------------------------ reads

    /// <summary>Everything the task page shows: ancestors, children, both directions of links, and what the task is waiting on.</summary>
    public async Task<TaskStructure> GetStructureAsync(TaskItem task, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var ancestors = await LoadAncestorsAsync(task.ParentTaskId, ct);
        var children = await db.Tasks.AsNoTracking()
            .Include(t => t.Department).Include(t => t.Assignee)
            .Where(t => t.ParentTaskId == task.Id)
            .OrderBy(t => t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled)
            .ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Title)
            .ToListAsync(ct);
        var links = await db.TaskDependencies.AsNoTracking()
            .Include(l => l.Predecessor).ThenInclude(t => t.Department)
            .Include(l => l.Successor).ThenInclude(t => t.Department)
            .Where(l => l.SuccessorTaskId == task.Id || l.PredecessorTaskId == task.Id)
            .ToListAsync(ct);

        var canEditThis = AccessPolicy.CanEditTask(actor, task);
        var predecessors = links.Where(l => l.SuccessorTaskId == task.Id)
            .Select(l => new DependencyView(l, l.Predecessor, DependencyRules.IsMet(l), DependencyRules.HasDateConflict(l), canEditThis))
            .OrderBy(v => v.Met).ThenBy(v => v.Other.Title).ToList();
        var successors = links.Where(l => l.PredecessorTaskId == task.Id)
            .Select(l => new DependencyView(l, l.Successor, DependencyRules.IsMet(l.Type, task.Status), DependencyRules.HasDateConflict(l),
                AccessPolicy.CanEditTask(actor, l.Successor)))
            .OrderBy(v => v.Other.Title).ToList();
        var waiting = await WaitingOnAsync(task, ancestors, links.Where(l => l.SuccessorTaskId == task.Id), ct);

        return new TaskStructure
        {
            Task = task, Ancestors = ancestors, Children = children,
            Predecessors = predecessors, Successors = successors, WaitingOn = waiting
        };
    }

    /// <summary>
    /// For task tables: which of these tasks are waiting, with a one-line reason. One query for the links (plus one per
    /// level of parents for the Todo tasks, which inherit their ancestors' start gates), not one per row.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, WaitingSummary>> GetWaitingAsync(IEnumerable<TaskItem> tasks, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, WaitingSummary>();
        var open = tasks.Where(t => t.IsOpen).DistinctBy(t => t.Id).ToList();
        if (open.Count == 0) return result;

        var parentOf = new Dictionary<Guid, Guid?>();
        var titleOf = new Dictionary<Guid, string>();
        foreach (var t in open) { parentOf[t.Id] = t.ParentTaskId; titleOf[t.Id] = t.Title; }
        var frontier = open.Where(t => t.Status == TaskItemStatus.Todo && t.ParentTaskId is not null)
            .Select(t => t.ParentTaskId!.Value).Distinct().Where(id => !parentOf.ContainsKey(id)).ToList();
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var rows = await db.Tasks.AsNoTracking().Where(t => frontier.Contains(t.Id))
                .Select(t => new { t.Id, t.Title, t.ParentTaskId }).ToListAsync(ct);
            foreach (var r in rows) { parentOf[r.Id] = r.ParentTaskId; titleOf[r.Id] = r.Title; }
            frontier = rows.Where(r => r.ParentTaskId is not null).Select(r => r.ParentTaskId!.Value)
                .Distinct().Where(id => !parentOf.ContainsKey(id)).ToList();
        }

        var successorIds = parentOf.Keys.ToList();
        var links = await db.TaskDependencies.AsNoTracking().Include(l => l.Predecessor)
            .Where(l => successorIds.Contains(l.SuccessorTaskId)).ToListAsync(ct);
        if (links.Count == 0) return result;
        var bySuccessor = links.ToLookup(l => l.SuccessorTaskId);

        foreach (var t in open)
        {
            var reasons = new List<string>();
            if (t.Status == TaskItemStatus.Todo)
            {
                reasons.AddRange(bySuccessor[t.Id].Where(l => l.Type.GatesStart() && !DependencyRules.IsMet(l)).Select(l => Describe(l, null)));
                var cursor = t.ParentTaskId;
                for (var guard = 0; cursor is Guid a && guard < MaxDepth; guard++)
                {
                    reasons.AddRange(bySuccessor[a].Where(l => l.Type.GatesStart() && !DependencyRules.IsMet(l))
                        .Select(l => Describe(l, titleOf.GetValueOrDefault(a))));
                    cursor = parentOf.GetValueOrDefault(a);
                }
            }
            else
            {
                reasons.AddRange(bySuccessor[t.Id].Where(l => !l.Type.GatesStart() && !DependencyRules.IsMet(l)).Select(l => Describe(l, null)));
            }
            if (reasons.Count > 0)
                result[t.Id] = new WaitingSummary(reasons.Count,
                    (t.Status == TaskItemStatus.Todo ? "Can't start until: " : "Can't finish until: ") + string.Join("; ", reasons));
        }
        return result;

        static string Describe(TaskDependency l, string? viaParent) =>
            $"\"{l.Predecessor.Title}\" to {(l.Type.WaitsForFinish() ? "finish" : "start")}" +
            (viaParent is null ? string.Empty : $" (via parent \"{viaParent}\")");
    }

    /// <summary>Tasks this one could be linked to: the rest of its project (any department). A standalone task has no candidates - dependencies need a project (§6.15).</summary>
    public async Task<IReadOnlyList<TaskItem>> ListLinkCandidatesAsync(TaskItem task, CancellationToken ct = default) =>
        task.ProjectId is null
            ? []
            : await ScopeQuery(task.ProjectId, task.DepartmentId).AsNoTracking().Include(t => t.Department)
                .Where(t => t.Id != task.Id && t.Status != TaskItemStatus.Cancelled)
                .OrderBy(t => t.Title).Take(500).ToListAsync(ct);

    /// <summary>
    /// Options for the Parent task picker: open tasks within the actor's tasks.view scope (§6.5). The form filters them by project client-side.
    /// </summary>
    public async Task<IReadOnlyList<ParentCandidate>> ListParentCandidatesAsync(Guid? excludeTaskId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var q = db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);
        q = Scoping.Tasks(q, actor);
        if (excludeTaskId is Guid exclude) q = q.Where(t => t.Id != exclude && t.ParentTaskId != exclude);
        var rows = await q
            .OrderBy(t => t.Project == null ? 1 : 0).ThenBy(t => t.Project!.Name).ThenBy(t => t.Title)
            .Take(1000)
            .Select(t => new { t.Id, t.Title, t.ProjectId, ProjectName = t.Project == null ? null : t.Project.Name, t.DepartmentId, DepartmentName = t.Department.Name })
            .ToListAsync(ct);
        return rows.Select(r => new ParentCandidate(r.Id, r.Title, r.ProjectId, r.ProjectName, r.DepartmentId, r.DepartmentName)).ToList();
    }

    /// <summary>Every link on a project - the data a Gantt is drawn from. The caller has already checked the project is visible.</summary>
    public async Task<IReadOnlyList<TaskDependency>> ListForProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await db.TaskDependencies.AsNoTracking().Include(l => l.Predecessor).Include(l => l.Successor)
            .Where(l => l.Successor.ProjectId == projectId)
            .OrderBy(l => l.Successor.Title).ThenBy(l => l.Predecessor.Title).ToListAsync(ct);

    /// <summary>The chain of parents above a task, root first.</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadAncestorsAsync(Guid? parentId, CancellationToken ct = default)
    {
        var chain = new List<TaskItem>();
        var seen = new HashSet<Guid>();
        var cursor = parentId;
        while (cursor is Guid id && seen.Add(id) && chain.Count < MaxDepth)
        {
            var t = await db.Tasks.AsNoTracking().Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) break;
            chain.Add(t);
            cursor = t.ParentTaskId;
        }
        chain.Reverse();
        return chain;
    }

    // ------------------------------------------------------------------ links

    /// <summary>Add a link. Needs edit rights on the successor (the task the link constrains); both tasks in one scope; no loops.</summary>
    public async Task<TaskDependency> AddAsync(DependencyInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (input.PredecessorTaskId == input.SuccessorTaskId) throw new ValidationException("A task can't depend on itself.");
        if (Math.Abs(input.LagDays) > 3650) throw new ValidationException("Lag must be between -3650 and 3650 days.");
        var pred = await db.Tasks.Include(t => t.Department).FirstOrDefaultAsync(t => t.Id == input.PredecessorTaskId, ct)
            ?? throw new NotFoundException("Predecessor task not found.");
        var succ = await db.Tasks.Include(t => t.Department).FirstOrDefaultAsync(t => t.Id == input.SuccessorTaskId, ct)
            ?? throw new NotFoundException("Successor task not found.");
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, succ), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, succ), "Only someone who can edit the waiting task may add a dependency to it.");
        RequireSameScope(pred, succ);
        if (await db.TaskDependencies.AnyAsync(l => l.PredecessorTaskId == pred.Id && l.SuccessorTaskId == succ.Id, ct))
            throw new ValidationException($"\"{succ.Title}\" already waits on \"{pred.Title}\".");

        var graph = await LoadScopeAsync(succ.ProjectId, succ.DepartmentId, ct);
        if (graph.IsAncestorOf(pred.Id, succ.Id) || graph.IsAncestorOf(succ.Id, pred.Id))
            throw new ValidationException("A task can't depend on its own parent task or one of its subtasks.");
        // Adding pred -> succ closes a loop if succ already reaches pred (through links, or through parents, which are implicit predecessors of their children).
        var path = graph.FindPath(succ.Id, pred.Id);
        if (path is not null)
            throw new ValidationException($"That would create a circular dependency: {graph.DescribePath(path)} → \"{succ.Title}\".");

        var link = new TaskDependency
        {
            PredecessorTaskId = pred.Id, SuccessorTaskId = succ.Id, Type = input.Type, LagDays = input.LagDays, CreatedById = actor.UserId
        };
        db.TaskDependencies.Add(link);
        AuditLink(actor, AuditAction.DependencyAdded, link, pred, succ);
        await db.SaveChangesAsync(ct);
        link.Predecessor = pred;
        link.Successor = succ;
        return link;
    }

    public async Task RemoveAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await db.TaskDependencies.Include(l => l.Predecessor).Include(l => l.Successor)
            .FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw new NotFoundException("Dependency not found.");
        await RemoveLinkAsync(link, ct);
    }

    public async Task RemoveAsync(Guid predecessorId, Guid successorId, CancellationToken ct = default)
    {
        var link = await db.TaskDependencies.Include(l => l.Predecessor).Include(l => l.Successor)
            .FirstOrDefaultAsync(l => l.PredecessorTaskId == predecessorId && l.SuccessorTaskId == successorId, ct)
            ?? throw new NotFoundException("Dependency not found.");
        await RemoveLinkAsync(link, ct);
    }

    private async Task RemoveLinkAsync(TaskDependency link, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanViewTask(actor, link.Successor), "This task belongs to another department.");
        AccessPolicy.Require(AccessPolicy.CanEditTask(actor, link.Successor), "Only someone who can edit the waiting task may remove its dependency.");
        db.TaskDependencies.Remove(link);
        AuditLink(actor, AuditAction.DependencyRemoved, link, link.Predecessor, link.Successor);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ rules TaskService applies

    /// <summary>
    /// Throws when a status change is gated (§6.15): a parent can't close while a child is open; FS/SS links (the task's
    /// own and its ancestors') gate leaving Todo; FF/SF links gate Done. Cancelling is never gated by links.
    /// </summary>
    public async Task EnsureStatusChangeAllowedAsync(TaskItem task, TaskItemStatus to, CancellationToken ct = default)
    {
        if (to == task.Status) return;
        if (to.IsClosed())
        {
            var openChildren = await db.Tasks.AsNoTracking()
                .Where(t => t.ParentTaskId == task.Id && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
                .OrderBy(t => t.Title).Select(t => t.Title).ToListAsync(ct);
            if (openChildren.Count > 0)
                throw new ValidationException(
                    $"\"{task.Title}\" can't be {(to == TaskItemStatus.Done ? "completed" : "cancelled")} while it has open subtasks ({Titles(openChildren)}). Close them first.");
        }
        var starting = DependencyRules.IsStart(task.Status, to);
        var finishing = DependencyRules.IsFinish(task.Status, to);
        if (!starting && !finishing) return;

        IReadOnlyList<TaskItem> ancestors = starting ? await LoadAncestorsAsync(task.ParentTaskId, ct) : [];
        var ids = new List<Guid> { task.Id };
        ids.AddRange(ancestors.Select(a => a.Id));
        var links = await db.TaskDependencies.AsNoTracking()
            .Include(l => l.Predecessor).ThenInclude(p => p.Department)
            .Include(l => l.Successor)
            .Where(l => ids.Contains(l.SuccessorTaskId)).ToListAsync(ct);
        if (starting)
        {
            var unmet = links.Where(l => l.Type.GatesStart() && !DependencyRules.IsMet(l)).ToList();
            if (unmet.Count > 0) throw new ValidationException($"Can't start \"{task.Title}\": waiting on {Reasons(unmet, task.Id)}.");
        }
        if (finishing)
        {
            var unmet = links.Where(l => l.SuccessorTaskId == task.Id && !l.Type.GatesStart() && !DependencyRules.IsMet(l)).ToList();
            if (unmet.Count > 0) throw new ValidationException($"Can't finish \"{task.Title}\": waiting on {Reasons(unmet, task.Id)}.");
        }
    }

    /// <summary>
    /// Validates the parent for a task being created or saved into the given project/department (§6.15). Returns the
    /// parent, or null for a top-level task. <paramref name="child"/> is null on create.
    /// </summary>
    public async Task<TaskItem?> ValidateParentAsync(TaskItem? child, Guid? parentId, Guid? projectId, Guid departmentId, bool childOpen, CancellationToken ct = default)
    {
        if (parentId is not Guid pid) return null;
        if (child is not null && pid == child.Id) throw new ValidationException("A task can't be its own parent.");
        var parent = await db.Tasks.AsNoTracking().Include(t => t.Department).FirstOrDefaultAsync(t => t.Id == pid, ct)
            ?? throw new NotFoundException("Parent task not found.");
        if (parent.ProjectId != projectId)
            throw new ValidationException(projectId is null
                ? $"A standalone task can only be filed under a standalone parent (\"{parent.Title}\" is on a project)."
                : $"The parent must be on the same project as the task (\"{parent.Title}\" isn't).");
        if (projectId is null && parent.DepartmentId != departmentId)
            throw new ValidationException("A standalone task's parent must be in the same department.");
        if (parent.Status.IsClosed() && childOpen)
            throw new ValidationException($"\"{parent.Title}\" is {parent.Status.Label().ToLowerInvariant()}; an open task can't be filed under it.");
        if (child is null || child.ParentTaskId == pid) return parent;

        // A task moving into this scope brings nothing with it that could loop (PrepareMoveAsync rejects crossing links).
        var movingScope = child.ProjectId != projectId || (projectId is null && child.DepartmentId != departmentId);
        if (movingScope) return parent;

        // Re-parenting inside the scope: the parent can't sit below the child, no link may tie the child's subtree to the
        // parent's chain (a task can't depend on its own ancestor or descendant), and the implicit parent -> child edge
        // must not close a loop with existing links.
        var graph = await LoadScopeAsync(projectId, departmentId, ct);
        graph.DetachParent(child.Id);
        if (graph.IsAncestorOf(child.Id, pid))
            throw new ValidationException($"\"{parent.Title}\" is a subtask of this task, so it can't also be its parent.");
        var subtree = graph.SubtreeIds(child.Id);
        var chain = graph.AncestorIds(pid);
        chain.Add(pid);
        var tie = graph.Links.FirstOrDefault(l => (subtree.Contains(l.Pred) && chain.Contains(l.Succ)) || (chain.Contains(l.Pred) && subtree.Contains(l.Succ)));
        if (tie != default)
            throw new ValidationException(
                $"\"{graph.Title(tie.Succ)}\" waits on \"{graph.Title(tie.Pred)}\", and a task can't depend on its own parent task or subtask. Remove that dependency first.");
        var path = graph.FindPath(child.Id, pid);
        if (path is not null)
            throw new ValidationException($"That would create a circular dependency through the parent: {graph.DescribePath(path)} → \"{child.Title}\".");
        return parent;
    }

    /// <summary>
    /// Before a task's project (or, for a standalone task, its department) changes: a child can't leave its parent on its
    /// own; a parent takes its subtree along, which needs edit rights on all of it; subtasks from other departments can't
    /// follow a task out of its project; and no link may end up crossing scopes. Returns the descendants that move with the
    /// task, tracked so the caller can update them in the same save.
    /// </summary>
    public async Task<IReadOnlyList<TaskItem>> PrepareMoveAsync(TaskItem task, Guid? newProjectId, Guid newDepartmentId, Guid? newParentId, Actor actor, CancellationToken ct = default)
    {
        var scopeChanges = newProjectId != task.ProjectId || (newProjectId is null && newDepartmentId != task.DepartmentId);
        if (!scopeChanges) return [];

        if (task.ParentTaskId is Guid currentParent && newParentId == currentParent)
        {
            var parentTitle = await db.Tasks.AsNoTracking().Where(t => t.Id == currentParent).Select(t => t.Title).FirstOrDefaultAsync(ct);
            throw new ValidationException($"This task is a subtask of \"{parentTitle}\" and can't be moved on its own. Move the parent instead, or detach it first.");
        }

        var descendants = await LoadDescendantsAsync(task.Id, ct);
        var blocked = descendants.Where(d => !AccessPolicy.CanEditTask(actor, d)).Select(d => d.Title).ToList();
        if (blocked.Count > 0)
            throw new ValidationException($"Moving \"{task.Title}\" would take its subtasks along, and you can't edit {Titles(blocked)}.");
        if (newProjectId is null)
        {
            var foreign = descendants.Where(d => d.DepartmentId != newDepartmentId).Select(d => d.Title).ToList();
            if (foreign.Count > 0)
                throw new ValidationException($"Subtasks from other departments ({Titles(foreign)}) can't follow \"{task.Title}\" out of its project. Detach them first.");
        }

        var moving = descendants.Select(d => d.Id).Append(task.Id).ToList();
        // Leaving a project: a standalone task can't have dependencies at all, so every link on the moving tasks - not only
        // the ones that would cross - has to go first (§6.15).
        var crossing = await db.TaskDependencies.AsNoTracking().Include(l => l.Predecessor).Include(l => l.Successor)
            .Where(l => newProjectId == null
                ? moving.Contains(l.PredecessorTaskId) || moving.Contains(l.SuccessorTaskId)
                : (moving.Contains(l.PredecessorTaskId) && !moving.Contains(l.SuccessorTaskId))
                  || (!moving.Contains(l.PredecessorTaskId) && moving.Contains(l.SuccessorTaskId)))
            .ToListAsync(ct);
        if (crossing.Count > 0)
            throw new ValidationException((newProjectId is null
                    ? "A standalone task can't have dependencies. Remove these first: "
                    : "Remove the dependencies that would then cross projects first: ") +
                string.Join(", ", crossing.Select(l => $"\"{l.Successor.Title}\" waits on \"{l.Predecessor.Title}\"")) + ".");
        return descendants;
    }

    // ------------------------------------------------------------------ helpers

    private IQueryable<TaskItem> ScopeQuery(Guid? projectId, Guid departmentId) =>
        projectId is Guid pid
            ? db.Tasks.Where(t => t.ProjectId == pid)
            : db.Tasks.Where(t => t.ProjectId == null && t.DepartmentId == departmentId);

    /// <summary>Dependencies exist only between tasks on the same project (§6.15); a standalone task has none.</summary>
    private static void RequireSameScope(TaskItem a, TaskItem b)
    {
        if (a.ProjectId is null || b.ProjectId is null)
            throw new ValidationException("Dependencies are only available between tasks on a project; a standalone task can't be linked.");
        if (a.ProjectId != b.ProjectId)
            throw new ValidationException("Both tasks must be on the same project.");
    }

    /// <summary>All tasks below a root, tracked (they are what a move updates).</summary>
    private async Task<List<TaskItem>> LoadDescendantsAsync(Guid rootId, CancellationToken ct)
    {
        var all = new List<TaskItem>();
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var rows = await db.Tasks.Include(t => t.Department)
                .Where(t => t.ParentTaskId != null && frontier.Contains(t.ParentTaskId.Value)).ToListAsync(ct);
            frontier = new List<Guid>();
            foreach (var r in rows)
                if (seen.Add(r.Id)) { all.Add(r); frontier.Add(r.Id); }
        }
        return all;
    }

    private async Task<IReadOnlyList<WaitReason>> WaitingOnAsync(TaskItem task, IReadOnlyList<TaskItem> ancestors, IEnumerable<TaskDependency> ownLinks, CancellationToken ct)
    {
        if (!task.IsOpen) return [];
        var reasons = new List<WaitReason>();
        if (task.Status == TaskItemStatus.Todo)
        {
            reasons.AddRange(ownLinks.Where(l => l.Type.GatesStart() && !DependencyRules.IsMet(l)).Select(l => new WaitReason(l, l.Predecessor, null)));
            if (ancestors.Count > 0)
            {
                var ancestorIds = ancestors.Select(a => a.Id).ToList();
                var inherited = await db.TaskDependencies.AsNoTracking()
                    .Include(l => l.Predecessor).ThenInclude(t => t.Department)
                    .Include(l => l.Successor)
                    .Where(l => ancestorIds.Contains(l.SuccessorTaskId)).ToListAsync(ct);
                reasons.AddRange(inherited.Where(l => l.Type.GatesStart() && !DependencyRules.IsMet(l)).Select(l => new WaitReason(l, l.Predecessor, l.Successor)));
            }
        }
        else
        {
            reasons.AddRange(ownLinks.Where(l => !l.Type.GatesStart() && !DependencyRules.IsMet(l)).Select(l => new WaitReason(l, l.Predecessor, null)));
        }
        return reasons;
    }

    private static string Reasons(IEnumerable<TaskDependency> unmet, Guid taskId) =>
        string.Join(", ", unmet.Select(l => new WaitReason(l, l.Predecessor, l.SuccessorTaskId == taskId ? null : l.Successor).Describe()));

    private static string Titles(IEnumerable<string> titles) => string.Join(", ", titles.Select(t => $"\"{t}\""));

    private void AuditLink(Actor actor, string action, TaskDependency link, TaskItem pred, TaskItem succ)
    {
        audit.Add(actor, AuditEntity.Task, succ.Id, action, succ.DepartmentId, succ.Title, Details("successor"));
        audit.Add(actor, AuditEntity.Task, pred.Id, action, pred.DepartmentId, pred.Title, Details("predecessor"));

        object Details(string role) => new
        {
            dependencyId = link.Id, role,
            predecessorId = pred.Id, predecessor = pred.Title,
            successorId = succ.Id, successor = succ.Title,
            type = link.Type, lagDays = link.LagDays
        };
    }

    /// <summary>The tasks and links of one scope, loaded once for tree and cycle checks.</summary>
    private async Task<Graph> LoadScopeAsync(Guid? projectId, Guid departmentId, CancellationToken ct)
    {
        var nodes = await ScopeQuery(projectId, departmentId).AsNoTracking()
            .Select(t => new { t.Id, t.Title, t.ParentTaskId }).ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToList();
        var links = await db.TaskDependencies.AsNoTracking()
            .Where(l => ids.Contains(l.SuccessorTaskId) && ids.Contains(l.PredecessorTaskId))
            .Select(l => new { l.PredecessorTaskId, l.SuccessorTaskId }).ToListAsync(ct);
        var graph = new Graph();
        foreach (var n in nodes) graph.AddNode(n.Id, n.Title, n.ParentTaskId);
        foreach (var l in links) graph.AddLink(l.PredecessorTaskId, l.SuccessorTaskId);
        return graph;
    }

    /// <summary>
    /// In-memory view of a scope. Edges run predecessor → successor for links and parent → child for the tree (a parent is
    /// an implicit predecessor of its children, so inherited start gates can't loop either).
    /// </summary>
    private sealed class Graph
    {
        private readonly Dictionary<Guid, string> _titles = new();
        private readonly Dictionary<Guid, Guid?> _parents = new();
        private readonly List<(Guid Pred, Guid Succ)> _links = new();

        public IReadOnlyList<(Guid Pred, Guid Succ)> Links => _links;

        public void AddNode(Guid id, string title, Guid? parentId) { _titles[id] = title; _parents[id] = parentId; }
        public void AddLink(Guid pred, Guid succ) => _links.Add((pred, succ));
        public void DetachParent(Guid id) { if (_parents.ContainsKey(id)) _parents[id] = null; }
        public string Title(Guid id) => _titles.GetValueOrDefault(id) ?? id.ToString();

        public HashSet<Guid> AncestorIds(Guid id)
        {
            var set = new HashSet<Guid>();
            var cursor = _parents.GetValueOrDefault(id);
            for (var guard = 0; cursor is Guid p && guard < MaxDepth && set.Add(p); guard++)
                cursor = _parents.GetValueOrDefault(p);
            return set;
        }

        public bool IsAncestorOf(Guid ancestor, Guid id) => AncestorIds(id).Contains(ancestor);

        public HashSet<Guid> SubtreeIds(Guid root)
        {
            var set = new HashSet<Guid> { root };
            var frontier = new List<Guid> { root };
            for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
                frontier = _parents.Where(kv => kv.Value is Guid p && frontier.Contains(p) && set.Add(kv.Key)).Select(kv => kv.Key).ToList();
            return set;
        }

        /// <summary>Breadth-first search along the edges; the path from <paramref name="from"/> to <paramref name="to"/>, or null.</summary>
        public List<Guid>? FindPath(Guid from, Guid to)
        {
            var next = new Dictionary<Guid, List<Guid>>();
            foreach (var (id, parent) in _parents) if (parent is Guid p) Edge(p, id);
            foreach (var (pred, succ) in _links) Edge(pred, succ);

            var previous = new Dictionary<Guid, Guid> { [from] = from };
            var queue = new Queue<Guid>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == to)
                {
                    var path = new List<Guid>();
                    for (var n = to; ; n = previous[n]) { path.Add(n); if (n == from) break; }
                    path.Reverse();
                    return path;
                }
                if (!next.TryGetValue(current, out var outgoing)) continue;
                foreach (var n in outgoing)
                    if (previous.TryAdd(n, current)) queue.Enqueue(n);
            }
            return null;

            void Edge(Guid a, Guid b)
            {
                if (!next.TryGetValue(a, out var list)) next[a] = list = new List<Guid>();
                list.Add(b);
            }
        }

        public string DescribePath(IEnumerable<Guid> path) => string.Join(" → ", path.Select(id => $"\"{Title(id)}\""));
    }
}

using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>Input for adding a dependency link (spec §6.15): the successor waits on the predecessor.</summary>
public sealed class DependencyInput
{
    public Guid PredecessorTaskId { get; set; }
    public Guid SuccessorTaskId { get; set; }
    public DependencyType Type { get; set; } = DependencyType.FinishToStart;
    /// <summary>Calendar days between the two ends; negative is a lead.</summary>
    public int LagDays { get; set; }
}

/// <summary>A dependency link seen from one task's side. <see cref="Other"/> is the task at the far end.</summary>
public sealed record DependencyView(TaskDependency Link, TaskItem Other, bool Met, bool DateConflict, bool CanRemove);

/// <summary>Why a task can't take its next step: an unmet link, either its own or inherited from an ancestor (<see cref="ViaAncestor"/>).</summary>
public sealed record WaitReason(TaskDependency Link, TaskItem Predecessor, TaskItem? ViaAncestor)
{
    /// <summary>E.g. <c>"Provision the servers" (IT) to finish</c>, with <c>(via parent "Phase 2")</c> when inherited.</summary>
    public string Describe()
    {
        var dept = Predecessor.Department is null ? string.Empty : $" ({Predecessor.Department.Name})";
        var via = ViaAncestor is null ? string.Empty : $" (via parent \"{ViaAncestor.Title}\")";
        return $"\"{Predecessor.Title}\"{dept} to {(Link.Type.WaitsForFinish() ? "finish" : "start")}{via}";
    }
}

/// <summary>Everything the task page shows about a task's place in the tree and the dependency graph.</summary>
public sealed class TaskStructure
{
    public required TaskItem Task { get; init; }
    /// <summary>Root first, direct parent last.</summary>
    public IReadOnlyList<TaskItem> Ancestors { get; init; } = [];
    public IReadOnlyList<TaskItem> Children { get; init; } = [];
    /// <summary>Links this task waits on.</summary>
    public IReadOnlyList<DependencyView> Predecessors { get; init; } = [];
    /// <summary>Links waiting on this task.</summary>
    public IReadOnlyList<DependencyView> Successors { get; init; } = [];
    /// <summary>The unmet links that gate this task's <em>next</em> move (start while Todo, finish once started). Empty = not waiting.</summary>
    public IReadOnlyList<WaitReason> WaitingOn { get; init; } = [];
    public int ChildrenClosed => Children.Count(c => c.Status.IsClosed());
    public bool HasOpenChildren => Children.Any(c => c.IsOpen);
    public bool HasDateConflict => Predecessors.Any(p => p.DateConflict) || Successors.Any(s => s.DateConflict);
}

/// <summary>For task tables: how many unmet links gate a task's next move, and a one-line reason for the badge tooltip.</summary>
public sealed record WaitingSummary(int Count, string Reason);

/// <summary>An option for the Parent task picker; the project and department let the form filter client-side.</summary>
public sealed record ParentCandidate(Guid Id, string Title, Guid? ProjectId, string? ProjectName, Guid DepartmentId, string DepartmentName);

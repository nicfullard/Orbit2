namespace Orbit.Data.Entities;

/// <summary>
/// A link between two tasks (spec §6.15): the <see cref="Successor"/> waits for the <see cref="Predecessor"/>.
/// The <see cref="Type"/> says which ends are tied (finish-to-start etc.); it gates the successor's status changes
/// and, with <see cref="LagDays"/>, drives the planned-date check. One link per ordered pair.
/// </summary>
public class TaskDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The task that has to start or finish first.</summary>
    public Guid PredecessorTaskId { get; set; }
    public TaskItem Predecessor { get; set; } = null!;
    /// <summary>The task that waits.</summary>
    public Guid SuccessorTaskId { get; set; }
    public TaskItem Successor { get; set; } = null!;
    public DependencyType Type { get; set; } = DependencyType.FinishToStart;
    /// <summary>Calendar days between the two ends; negative is a lead. Used by the date check only, never by the workflow gate.</summary>
    public int LagDays { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null when added by the API (Claude).</summary>
    public Guid? CreatedById { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
}

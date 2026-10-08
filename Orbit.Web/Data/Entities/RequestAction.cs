namespace Orbit.Data.Entities;

/// <summary>
/// A script in the action library (spec §6.20): C# (Roslyn) that an Action step runs with the request's values and the parameters
/// the step binds. Written by someone holding actions.create; placed in flows by whoever configures them. Runs as the Orbit web
/// process or as an Orbit Agent's service account, unsandboxed - the library is the trust boundary, not the script.
/// </summary>
public class RequestAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ActionRunsOn RunsOn { get; set; } = ActionRunsOn.Web;
    /// <summary>For an agent-side action: the one agent that runs it, or null for any connected agent that can.</summary>
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string Script { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestActionParameter> Parameters { get; set; } = new List<RequestActionParameter>();
    /// <summary>The steps that run it; an action in use can't be deleted.</summary>
    public ICollection<RequestFlowStep> Steps { get; set; } = new List<RequestFlowStep>();
}

/// <summary>A value an action takes (§6.20), which each step that uses the action binds to a template: <c>Inputs["code"]</c> in the script.</summary>
public class RequestActionParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActionId { get; set; }
    public RequestAction Action { get; set; } = null!;
    /// <summary>Lower-case letters, digits and hyphens; unique within the action.</summary>
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

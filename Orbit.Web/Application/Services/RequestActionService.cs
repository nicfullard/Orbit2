using Microsoft.EntityFrameworkCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Scripting;

namespace Orbit.Application.Services;

/// <summary>An agent as the action editor offers it: whether it is connected right now, and whether it is new enough to run scripts.</summary>
public sealed record ActionAgentChoice(Agent Agent, bool Online, bool RunsScripts);

/// <summary>
/// The action library (spec §6.20, Admin &gt; Actions): the scripts request flows run, written and kept by whoever holds actions.create.
/// A script is compiled when it is saved, so a typo is caught here rather than in a request. The audit log records a script's hash
/// and length, never its text: scripts can quote connection names and table layouts.
/// </summary>
public sealed class RequestActionService(
    ApplicationDbContext db, IActorProvider actors, AuditService audit, ScriptHost scripts, AgentConnectionRegistry registry)
{
    public async Task<IReadOnlyList<RequestAction>> ListAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.RequestActions.AsNoTracking()
            .Include(a => a.Parameters.OrderBy(p => p.DisplayOrder))
            .Include(a => a.Agent)
            .Include(a => a.Steps).ThenInclude(s => s.Flow)
            .OrderBy(a => a.Name)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public async Task<RequestAction> GetAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.RequestActions.AsNoTracking()
            .Include(a => a.Parameters.OrderBy(p => p.DisplayOrder))
            .Include(a => a.Agent)
            .Include(a => a.Steps).ThenInclude(s => s.Flow).ThenInclude(f => f.Category)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Action not found.");
    }

    /// <summary>The active agents, each with whether it is connected and announces <see cref="AgentCapabilities.ScriptRun"/>.</summary>
    public async Task<IReadOnlyList<ActionAgentChoice>> AgentsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.Status == AgentStatus.Active).OrderBy(a => a.Name).ToListAsync(ct);
        return agents.Select(a =>
        {
            var connection = registry.Find(a.Id);
            return new ActionAgentChoice(a, connection is not null, connection?.Capabilities.Contains(AgentCapabilities.ScriptRun) == true);
        }).ToList();
    }

    /// <summary>The script's compile errors, for the editor's "Check" button; empty when it compiles.</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(string? script, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return string.IsNullOrWhiteSpace(script) ? ["The script is empty."] : scripts.Check(script);
    }

    public async Task<RequestAction> CreateAsync(RequestActionInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var name = RequestFlowRules.RequireActionName(input.Name);
        await RequireUniqueNameAsync(name, null, ct);
        var (description, runsOn, agentId, script) = await CleanAsync(input, ct);
        var parameters = RequestFlowRules.ValidateParameters(input.Parameters);

        var now = DateTime.UtcNow;
        var action = new RequestAction
        {
            Name = name, Description = description, RunsOn = runsOn, AgentId = agentId, Script = script, CreatedAt = now, UpdatedAt = now
        };
        var order = 1;
        foreach (var (key, label) in parameters)
            action.Parameters.Add(new RequestActionParameter { ActionId = action.Id, Key = key, Label = label, DisplayOrder = order++ });
        db.RequestActions.Add(action);
        audit.Add(actor, AuditEntity.RequestAction, action.Id, AuditAction.Created, null, action.Name, Describe(action));
        await db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<RequestAction> UpdateAsync(Guid id, RequestActionInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var action = await db.RequestActions.Include(a => a.Parameters).Include(a => a.Steps).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Action not found.");
        var name = RequestFlowRules.RequireActionName(input.Name);
        if (!string.Equals(name, action.Name, StringComparison.OrdinalIgnoreCase)) await RequireUniqueNameAsync(name, id, ct);
        var (description, runsOn, agentId, script) = await CleanAsync(input, ct);
        var parameters = RequestFlowRules.ValidateParameters(input.Parameters);

        var changes = new ChangeSet()
            .TrackText("name", action.Name, name)
            .TrackText("description", action.Description, description)
            .Track("runsOn", action.RunsOn, runsOn)
            .Track("agentId", action.AgentId, agentId)
            .Track("scriptHash", ScriptHost.Hash(action.Script), ScriptHost.Hash(script))
            .Track("parameters", string.Join(", ", action.Parameters.OrderBy(p => p.DisplayOrder).Select(p => p.Key)), string.Join(", ", parameters.Select(p => p.Key)));
        if (!changes.HasChanges) return action;

        action.Name = name;
        action.Description = description;
        action.RunsOn = runsOn;
        action.AgentId = agentId;
        action.Script = script;
        action.UpdatedAt = DateTime.UtcNow;
        // Parameters are matched by key: a kept key keeps its row (and the steps' bindings to it), a new key gets a row, a dropped one goes.
        var keep = parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        db.RequestActionParameters.RemoveRange(action.Parameters.Where(p => !keep.Contains(p.Key)));
        var order = 1;
        foreach (var (key, label) in parameters)
        {
            var row = action.Parameters.FirstOrDefault(p => p.Key == key);
            if (row is null) db.RequestActionParameters.Add(new RequestActionParameter { ActionId = action.Id, Key = key, Label = label, DisplayOrder = order });
            else { row.Label = label; row.DisplayOrder = order; }
            order++;
        }
        audit.Add(actor, AuditEntity.RequestAction, action.Id, AuditAction.Updated, null, action.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return action;
    }

    /// <summary>Only an action no step runs can go.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var action = await db.RequestActions.Include(a => a.Steps).ThenInclude(s => s.Flow).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Action not found.");
        if (action.Steps.Count > 0)
            throw new ValidationException($"\"{action.Name}\" is used by {action.Steps.Count} {(action.Steps.Count == 1 ? "step" : "steps")} ({string.Join(", ", action.Steps.Select(s => s.Flow.Title).Distinct().Take(3))}). Remove it from them first.");
        db.RequestActions.Remove(action);
        audit.Add(actor, AuditEntity.RequestAction, action.Id, AuditAction.Deleted, null, action.Name);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(string? Description, ActionRunsOn RunsOn, Guid? AgentId, string Script)> CleanAsync(RequestActionInput input, CancellationToken ct)
    {
        var description = RequestFlowRules.Clean(input.Description, RequestFlowRules.MaxDescriptionLength, "The description");
        if (!Enum.IsDefined(input.RunsOn)) throw new ValidationException("Choose where the action runs.");
        Guid? agentId = null;
        if (input.RunsOn == ActionRunsOn.Agent && input.AgentId is Guid id && id != Guid.Empty)
        {
            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new ValidationException("That agent doesn't exist.");
            if (agent.Status != AgentStatus.Active) throw new ValidationException($"Agent \"{agent.Name}\" isn't active.");
            agentId = id;
        }
        var script = input.Script?.Replace("\r\n", "\n").Trim() ?? string.Empty;
        if (script.Length == 0) throw new ValidationException("The script is empty.");
        var errors = scripts.Check(script);
        if (errors.Count > 0) throw new ValidationException("The script doesn't compile: " + string.Join(" ", errors));
        return (description, input.RunsOn, agentId, script);
    }

    private static object Describe(RequestAction a) => new
    {
        a.Name, a.Description, runsOn = a.RunsOn, a.AgentId, scriptHash = ScriptHost.Hash(a.Script), scriptLength = a.Script.Length,
        parameters = a.Parameters.OrderBy(p => p.DisplayOrder).Select(p => p.Key).ToList()
    };

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var n = name.ToLowerInvariant();
        if (await db.RequestActions.AnyAsync(a => a.Name.ToLower() == n && a.Id != exceptId, ct))
            throw new ValidationException($"There is already an action called \"{name}\".");
    }

    private async Task<Actor> RequireAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanCreateActions(actor), "You don't have permission to manage the action library.");
        return actor;
    }
}

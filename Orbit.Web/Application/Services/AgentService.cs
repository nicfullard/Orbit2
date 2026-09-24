using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Management of on-premises Orbit Agents (agents.manage), plus the two operations an agent performs on itself
/// (redeeming its registration token, removing its own registration). See spec §6.14 and §8.2.
/// </summary>
public sealed class AgentService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    AgentConnectionRegistry registry,
    IOptions<AgentOptions> options)
{
    public async Task<IReadOnlyList<AgentListItem>> ListAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var agents = await db.Agents.AsNoTracking()
            .OrderBy(a => a.Status == AgentStatus.Revoked).ThenByDescending(a => a.CreatedAt).ToListAsync(ct);
        return agents.Select(a => new AgentListItem(a, a.Status == AgentStatus.Active && registry.IsOnline(a.Id))).ToList();
    }

    /// <summary>Creates a pending agent. The raw registration token is returned exactly once and never stored.</summary>
    public async Task<CreatedAgent> CreateAsync(string? name, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ValidationException("Name is required.");
        if (trimmed.Length > 200) throw new ValidationException("Name must be 200 characters or fewer.");

        var agent = new Agent { Name = trimmed, CreatedById = actor.UserId, CreatedAt = DateTime.UtcNow };
        var raw = IssueRegistrationToken(agent);
        db.Agents.Add(agent);
        audit.Add(actor, AuditEntity.Agent, agent.Id, AuditAction.Created, null, agent.Name);
        await db.SaveChangesAsync(ct);
        return new CreatedAgent(agent, raw);
    }

    /// <summary>A fresh token for an agent that was never configured (the old one expired or was lost).</summary>
    public async Task<CreatedAgent> RegenerateTokenAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Agent not found.");
        if (agent.Status != AgentStatus.Pending)
            throw new ValidationException("Only an agent that hasn't been configured yet can be given a new token. Revoke this one and create another.");
        var raw = IssueRegistrationToken(agent);
        audit.Add(actor, AuditEntity.Agent, agent.Id, AuditAction.Updated, null, agent.Name, new { registrationToken = "regenerated" });
        await db.SaveChangesAsync(ct);
        return new CreatedAgent(agent, raw);
    }

    /// <summary>Invalidates the agent's credential and drops its connection straight away.</summary>
    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Agent not found.");
        if (agent.Status == AgentStatus.Revoked) return;
        Revoke(agent);
        audit.Add(actor, AuditEntity.Agent, agent.Id, AuditAction.Revoked, null, agent.Name);
        await db.SaveChangesAsync(ct);
        registry.Disconnect(agent.Id);
    }

    /// <summary>Removes a row that can no longer connect. Its audit entries stay.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Agent not found.");
        if (agent.Status == AgentStatus.Active)
            throw new ValidationException("Revoke the agent before deleting it.");
        db.Agents.Remove(agent);
        audit.Add(actor, AuditEntity.Agent, agent.Id, AuditAction.Deleted, null, agent.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Called by <c>Orbit.Agent configure</c> (no signed-in user): swaps a valid registration token for the agent's
    /// long-lived secret. Returns null for any invalid, expired or already-used token, without saying which.
    /// </summary>
    public async Task<AgentRegistrationResponse?> RegisterAsync(AgentRegistrationRequest request, string? remoteIp, CancellationToken ct = default)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith(ApiKeyHasher.AgentRegistrationTokenPrefix, StringComparison.Ordinal))
            return null;

        var tokenHash = ApiKeyHasher.Hash(token);
        var secret = ApiKeyHasher.GenerateRawKey(ApiKeyHasher.AgentSecretPrefix);
        var secretHash = ApiKeyHasher.Hash(secret);
        var secretPrefix = secret[..Math.Min(ApiKeyHasher.AgentSecretPrefix.Length + 6, secret.Length)];
        var now = DateTime.UtcNow;

        // One statement, so a token can be redeemed once even if two configure runs race.
        var redeemed = await db.Agents
            .Where(a => a.RegistrationTokenHash == tokenHash && a.Status == AgentStatus.Pending && a.RegistrationExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AgentStatus.Active)
                .SetProperty(a => a.HashedSecret, secretHash)
                .SetProperty(a => a.SecretPrefix, secretPrefix)
                .SetProperty(a => a.RegistrationTokenHash, (string?)null)
                .SetProperty(a => a.RegistrationExpiresAt, (DateTime?)null)
                .SetProperty(a => a.RegisteredAt, now)
                .SetProperty(a => a.MachineName, Clip(request.MachineName, 200))
                .SetProperty(a => a.OsDescription, Clip(request.OsDescription, 200))
                .SetProperty(a => a.Version, Clip(request.Version, 50))
                .SetProperty(a => a.LastIpAddress, remoteIp), ct);
        if (redeemed != 1) return null;

        var agent = await db.Agents.AsNoTracking().FirstAsync(a => a.HashedSecret == secretHash, ct);
        audit.Add(Actor.System, AuditEntity.Agent, agent.Id, AuditAction.Registered, null, agent.Name,
            new { machine = agent.MachineName, os = agent.OsDescription, version = agent.Version, ip = remoteIp });
        await db.SaveChangesAsync(ct);
        return new AgentRegistrationResponse { AgentId = agent.Id, Name = agent.Name, Secret = secret };
    }

    /// <summary>Called by <c>Orbit.Agent remove</c>, authenticated as the agent itself.</summary>
    public async Task UnregisterAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null || agent.Status == AgentStatus.Revoked) return;
        Revoke(agent);
        audit.Add(Actor.System, AuditEntity.Agent, agent.Id, AuditAction.Revoked, null, agent.Name, new { by = "agent" });
        await db.SaveChangesAsync(ct);
        registry.Disconnect(agent.Id);
    }

    private string IssueRegistrationToken(Agent agent)
    {
        var raw = ApiKeyHasher.GenerateRawKey(ApiKeyHasher.AgentRegistrationTokenPrefix);
        agent.RegistrationTokenHash = ApiKeyHasher.Hash(raw);
        agent.RegistrationExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, options.Value.RegistrationTokenLifetimeMinutes));
        return raw;
    }

    private static void Revoke(Agent agent)
    {
        agent.Status = AgentStatus.Revoked;
        agent.RevokedAt = DateTime.UtcNow;
        agent.HashedSecret = null;
        agent.RegistrationTokenHash = null;
        agent.RegistrationExpiresAt = null;
    }

    private static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageAgents(actor), "You don't have permission to manage agents.");
        return actor;
    }
}

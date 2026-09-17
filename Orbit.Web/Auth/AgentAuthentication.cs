using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Auth;

public static class AgentAuthenticationDefaults
{
    public const string Scheme = "Agent";
}

public sealed class AgentAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates an on-premises Orbit Agent with <c>Authorization: Bearer &lt;agent secret&gt;</c>.
/// The resulting principal identifies the agent and nothing else: no role, no department, so it can reach the
/// agent hub (<see cref="Policies.Agent"/>) but never a page or the MCP server.
/// </summary>
public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AgentAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApplicationDbContext db)
    : AuthenticationHandler<AgentAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var raw = auth["Bearer ".Length..].Trim();
        if (!raw.StartsWith(ApiKeyHasher.AgentSecretPrefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var hash = ApiKeyHasher.Hash(raw);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.HashedSecret == hash, Context.RequestAborted);
        if (agent is null || agent.Status != AgentStatus.Active) return AuthenticateResult.Fail("Unknown or revoked agent.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, agent.Id.ToString()),
            new(ClaimTypes.Name, agent.Name),
            new(OrbitClaims.AgentId, agent.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"Orbit Agent\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

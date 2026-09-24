using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Auth;

public static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "ApiKey";
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates machine callers (the MCP server) with <c>Authorization: Bearer &lt;key&gt;</c>.
/// The key's role id and DepartmentId become claims; the actor provider resolves the role's grants per request,
/// so the same permission checks that apply to signed-in users apply unchanged to Claude.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApplicationDbContext db)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = ExtractKey(Request);
        if (string.IsNullOrEmpty(raw)) return AuthenticateResult.NoResult();
        if (!raw.StartsWith(ApiKeyHasher.KeyPrefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var hash = ApiKeyHasher.Hash(raw);
        var key = await db.ApiKeys.AsNoTracking().Include(k => k.Role).FirstOrDefaultAsync(k => k.HashedKey == hash, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Invalid API key.");
        if (key.RevokedAt is not null) return AuthenticateResult.Fail("This API key has been revoked.");

        var now = DateTime.UtcNow;
        if (key.LastUsedAt is null || key.LastUsedAt < now.AddMinutes(-5))
        {
            await db.ApiKeys.Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), Context.RequestAborted);
        }

        var displayName = $"{WellKnownIds.ClaudeAgentDisplayName} ({key.Name})";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, WellKnownIds.ClaudeAgentUserId.ToString()),
            new(ClaimTypes.Name, displayName),
            new(ClaimTypes.Role, key.Role.Name ?? string.Empty),
            new(OrbitClaims.RoleId, key.RoleId.ToString()),
            new(OrbitClaims.DisplayName, displayName),
            new(OrbitClaims.ActorType, nameof(ActorType.Api)),
            new(OrbitClaims.ApiKeyId, key.Id.ToString())
        };
        if (key.DepartmentId is Guid dept)
            claims.Add(new Claim(OrbitClaims.DepartmentId, dept.ToString()));

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"Orbit MCP\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var header = request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}

using System.Security.Claims;
using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Auth;

namespace Orbit.Agents;

/// <summary>The two plain HTTP calls an agent makes outside its hub connection: registering and de-registering.</summary>
public static class AgentEndpoints
{
    public const string RegistrationRateLimit = "agent-registration";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by necessity: the registration token is the credential. It is 256 bits of randomness, single use
        // and short-lived; the rate limit is only there to keep a guessing loop from becoming database load.
        app.MapPost(AgentProtocol.RegisterPath, async (AgentRegistrationRequest request, AgentService agents, HttpContext http, CancellationToken ct) =>
            {
                var response = await agents.RegisterAsync(request, http.Connection.RemoteIpAddress?.ToString(), ct);
                return response is null
                    ? Results.Problem("The registration token is invalid, expired or has already been used.", statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Ok(response);
            })
            .AllowAnonymous()
            .RequireRateLimiting(RegistrationRateLimit);

        app.MapDelete(AgentProtocol.RegistrationPath, async (ClaimsPrincipal user, AgentService agents, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue(OrbitClaims.AgentId), out var agentId)) return Results.Forbid();
                await agents.UnregisterAsync(agentId, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Agent);

        return app;
    }
}

using LiveStream.Api.Security;
using LiveStream.Application.Distribution;
using LiveStream.Application.Distribution.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Multi-platform distribution endpoints (docs/09-api-specification.md, MASTER_BLUEPRINT.md §28.4).
///
/// Destinations hang off a session; linked provider accounts hang off a workspace, because an
/// operator links a channel once and reuses it. Authorization is enforced inside the application
/// services so no route here can forget it.
/// </summary>
public static class DestinationEndpoints
{
    public static IEndpointRouteBuilder MapDestinationEndpoints(this IEndpointRouteBuilder app)
    {
        MapSessionDestinations(app);
        MapProviderAccounts(app);
        return app;
    }

    private static void MapSessionDestinations(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/live-sessions/{id:guid}/destinations")
            .WithTags("Destinations")
            .RequireAuthorization();

        group.MapGet("/", async (
                Guid id,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken) =>
            Results.Ok(await destinations.ListAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("ListDestinations")
            .WithSummary("Lists the external platforms this session publishes to.");

        group.MapGet("/{destinationId:guid}", async (
                Guid id,
                Guid destinationId,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken) =>
            Results.Ok(await destinations.GetAsync(id, destinationId, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetDestination")
            .WithSummary("Returns one destination and its current status.");

        group.MapGet("/{destinationId:guid}/events", async (
                Guid id,
                Guid destinationId,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken,
                [FromQuery] int limit = 50) =>
            Results.Ok(await destinations.ListEventsAsync(id, destinationId, http.User.RequireUserId(), limit,
                cancellationToken)))
            .WithName("ListDestinationEvents")
            .WithSummary("Returns the audit trail for one destination.");

        group.MapPost("/", async (
                Guid id,
                [FromBody] CreateDestinationRequest request,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken) =>
            {
                var created = await destinations.CreateAsync(id, http.User.RequireUserId(), request, cancellationToken);
                return Results.Created($"/api/v1/live-sessions/{id}/destinations/{created.Id}", created);
            })
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("CreateDestination")
            .WithSummary("Adds an external platform to this session.");

        group.MapPatch("/{destinationId:guid}", async (
                Guid id,
                Guid destinationId,
                [FromBody] UpdateDestinationRequest request,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken) =>
            Results.Ok(await destinations.UpdateAsync(id, destinationId, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("UpdateDestination")
            .WithSummary("Renames a destination, rotates its stream key, or enables and disables it.");

        group.MapDelete("/{destinationId:guid}", async (
                Guid id,
                Guid destinationId,
                HttpContext http,
                DestinationService destinations,
                CancellationToken cancellationToken) =>
            {
                await destinations.DeleteAsync(id, destinationId, http.User.RequireUserId(), cancellationToken);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("DeleteDestination")
            .WithSummary("Removes a destination from this session.");

        // Start and stop are separate from the session lifecycle on purpose: an operator can retry a
        // single failed platform without touching a broadcast that is running perfectly well.
        group.MapPost("/{destinationId:guid}/start", async (
                Guid id,
                Guid destinationId,
                HttpContext http,
                DestinationOrchestrator orchestrator,
                CancellationToken cancellationToken) =>
            Results.Ok(await orchestrator.StartDestinationAsync(id, destinationId, http.User.RequireUserId(),
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("StartDestination")
            .WithSummary("Starts or retries one destination while the session is live.");

        group.MapPost("/{destinationId:guid}/stop", async (
                Guid id,
                Guid destinationId,
                HttpContext http,
                DestinationOrchestrator orchestrator,
                CancellationToken cancellationToken) =>
            Results.Ok(await orchestrator.StopDestinationAsync(id, destinationId, http.User.RequireUserId(),
                cancellationToken)))
            .WithName("StopDestination")
            .WithSummary("Stops one destination, leaving the session broadcasting.");
    }

    private static void MapProviderAccounts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/distribution")
            .WithTags("Destinations")
            .RequireAuthorization();

        // Anonymous would be defensible — the catalogue is static — but keeping it authenticated
        // avoids advertising which integrations a deployment has configured.
        group.MapGet("/providers", (DestinationService destinations) =>
            Results.Ok(destinations.DescribeProviders()))
            .WithName("ListDistributionProviders")
            .WithSummary("Lists supported platforms and how each can be configured.");

        group.MapGet("/workspaces/{workspaceId:guid}/accounts", async (
                Guid workspaceId,
                HttpContext http,
                ProviderAccountService accounts,
                CancellationToken cancellationToken) =>
            Results.Ok(await accounts.ListAsync(workspaceId, http.User.RequireUserId(), cancellationToken)))
            .WithName("ListProviderAccounts")
            .WithSummary("Lists the external accounts linked to a workspace.");

        group.MapPost("/workspaces/{workspaceId:guid}/accounts/{provider}/authorize", async (
                Guid workspaceId,
                string provider,
                [FromBody] BeginAuthorizationRequest request,
                HttpContext http,
                ProviderAccountService accounts,
                CancellationToken cancellationToken) =>
            Results.Ok(await accounts.BeginAuthorizationAsync(workspaceId, http.User.RequireUserId(), provider,
                request.RedirectUri, cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("BeginProviderAuthorization")
            .WithSummary("Returns the consent URL for linking an external account.");

        group.MapPost("/accounts/callback", async (
                [FromBody] CompleteProviderAuthorizationRequest request,
                HttpContext http,
                ProviderAccountService accounts,
                CancellationToken cancellationToken) =>
            Results.Ok(await accounts.CompleteAuthorizationAsync(http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("CompleteProviderAuthorization")
            .WithSummary("Completes account linking by exchanging the authorization code.");

        group.MapDelete("/accounts/{accountId:guid}", async (
                Guid accountId,
                HttpContext http,
                ProviderAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                await accounts.DisconnectAsync(accountId, http.User.RequireUserId(), cancellationToken);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("DisconnectProviderAccount")
            .WithSummary("Disconnects a linked account and erases its stored tokens.");
    }

    /// <summary>
    /// The redirect URI is supplied by the caller so the same API serves the studio, a desktop
    /// build, and local development. It is echoed into the provider request and stored with the
    /// state entry, so the callback must present the identical value.
    /// </summary>
    public sealed record BeginAuthorizationRequest(string RedirectUri);
}

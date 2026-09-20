using LiveStream.Api.Security;
using LiveStream.Application.Studio;
using LiveStream.Application.Studio.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// The studio's look and its prepared shots (docs/07-live-studio.md).
///
/// All of it is configuration owned by the session, so every route sits under the session and is
/// authorized against it — the same way sources and destinations are.
/// </summary>
public static class StudioEndpoints
{
    public static IEndpointRouteBuilder MapStudioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/live-sessions/{id:guid}")
            .WithTags("Studio")
            .RequireAuthorization();

        group.MapGet("/branding", async (
                Guid id,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            Results.Ok(await studio.GetBrandingAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetSessionBranding")
            .WithSummary("Returns the watermark and accent colour this session broadcasts with.");

        group.MapPut("/branding", async (
                Guid id,
                [FromBody] UpdateBrandingRequest request,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            Results.Ok(await studio.UpdateBrandingAsync(id, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.SessionCreation)
            .WithName("UpdateSessionBranding")
            .WithSummary("Updates the watermark and accent colour.");

        group.MapGet("/scenes", async (
                Guid id,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            Results.Ok(await studio.ListScenesAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("ListSessionScenes")
            .WithSummary("Lists the prepared shots for this session, in display order.");

        group.MapPost("/scenes", async (
                Guid id,
                [FromBody] SaveSceneRequest request,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            {
                var scene = await studio.AddSceneAsync(id, http.User.RequireUserId(), request,
                    cancellationToken);

                return Results.Created($"/api/v1/live-sessions/{id}/scenes/{scene.Id}", scene);
            })
            .RequireRateLimiting(RateLimitPolicies.SessionCreation)
            .WithName("AddSessionScene")
            .WithSummary("Saves a prepared shot.");

        group.MapPut("/scenes/{sceneId:guid}", async (
                Guid id,
                Guid sceneId,
                [FromBody] SaveSceneRequest request,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            Results.Ok(await studio.UpdateSceneAsync(id, sceneId, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.SessionCreation)
            .WithName("UpdateSessionScene")
            .WithSummary("Updates a prepared shot.");

        group.MapDelete("/scenes/{sceneId:guid}", async (
                Guid id,
                Guid sceneId,
                HttpContext http,
                StudioService studio,
                CancellationToken cancellationToken) =>
            {
                await studio.DeleteSceneAsync(id, sceneId, http.User.RequireUserId(), cancellationToken);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.SessionCreation)
            .WithName("DeleteSessionScene")
            .WithSummary("Removes a prepared shot.");

        return app;
    }
}

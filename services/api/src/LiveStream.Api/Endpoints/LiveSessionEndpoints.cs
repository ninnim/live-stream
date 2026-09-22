using LiveStream.Api.Security;
using LiveStream.Application.Media;
using LiveStream.Application.Recordings;
using LiveStream.Application.Sessions;
using LiveStream.Application.Sessions.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Live Session endpoints under <c>/api/v1/live-sessions</c> (docs/09-api-specification.md).
/// Every route requires authentication; per-session authorization is enforced inside the
/// application services so it cannot be forgotten at the transport layer.
/// </summary>
public static class LiveSessionEndpoints
{
    public static IEndpointRouteBuilder MapLiveSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/live-sessions")
            .WithTags("Live Sessions")
            .RequireAuthorization();

        // ---------------------------------------------------------------------------------
        // Reads
        // ---------------------------------------------------------------------------------

        group.MapGet("/", async (
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken,
                [FromQuery] string? status = null,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 20) =>
            Results.Ok(await sessions.ListAsync(http.User.RequireUserId(), status, page, pageSize, cancellationToken)))
            .WithName("ListLiveSessions")
            .WithSummary("Lists sessions across every workspace the caller belongs to.");

        group.MapGet("/{id:guid}", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.GetAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetLiveSession")
            .WithSummary("Returns one live session with its current health and playback URLs.");

        group.MapGet("/{id:guid}/status", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.GetStatusAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetLiveSessionStatus")
            .WithSummary("Returns authoritative session state and the transitions currently allowed.");

        group.MapGet("/{id:guid}/health", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.GetHealthAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetLiveSessionHealth")
            .WithSummary("Returns stream health: ingest state, bitrate, viewers and recovery window.");

        group.MapGet("/{id:guid}/events", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken,
                [FromQuery] int limit = 50) =>
            Results.Ok(await sessions.ListEventsAsync(id, http.User.RequireUserId(), limit, cancellationToken)))
            .WithName("ListLiveSessionEvents")
            .WithSummary("Returns the session's audit trail of state transitions and media events.");

        group.MapGet("/{id:guid}/recordings", async (
                Guid id,
                HttpContext http,
                RecordingService recordings,
                LiveSessionAuthorizationService authorization,
                CancellationToken cancellationToken) =>
            Results.Ok(await recordings.ListAsync(id, http.User.RequireUserId(), authorization, cancellationToken)))
            .WithName("ListLiveSessionRecordings")
            .WithSummary("Returns recording metadata for a session.");

        // ---------------------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------------------

        group.MapPost("/", async (
                [FromBody] CreateLiveSessionRequest request,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken,
                [FromQuery] Guid? workspaceId = null) =>
            {
                var created = await sessions.CreateAsync(http.User.RequireUserId(), workspaceId, request,
                    cancellationToken);
                return Results.Created($"/api/v1/live-sessions/{created.Id}", created);
            })
            .RequireRateLimiting(RateLimitPolicies.SessionCreation)
            .WithName("CreateLiveSession")
            .WithSummary("Creates a live session in DRAFT state.");

        group.MapPatch("/{id:guid}", async (
                Guid id,
                [FromBody] UpdateLiveSessionRequest request,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.UpdateAsync(id, http.User.RequireUserId(), request, cancellationToken)))
            .WithName("UpdateLiveSession")
            .WithSummary("Updates session details before the broadcast starts.");

        group.MapPost("/{id:guid}/prepare", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.PrepareAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("PrepareLiveSession")
            .WithSummary("Reserves media resources and moves the session to READY.");

        group.MapPost("/{id:guid}/start", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.StartAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("StartLiveSession")
            .WithSummary("Requests LIVE. The session is promoted once the media plane confirms ingest.");

        group.MapPost("/{id:guid}/stop", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.StopAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("StopLiveSession")
            .WithSummary("Stops the broadcast, finalizes recording, and ends the session.");

        // ---------------------------------------------------------------------------------
        // Broadcaster
        // ---------------------------------------------------------------------------------

        group.MapPost("/{id:guid}/sources/credentials", async (
                Guid id,
                HttpContext http,
                IngestCredentialService credentials,
                CancellationToken cancellationToken) =>
            Results.Ok(await credentials.IssueAsync(id, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("IssueIngestCredential")
            .WithSummary("Issues a short-lived, path-scoped credential for browser broadcasting.");

        // Issuing and rotating are one endpoint because they are one action: the key is shown once,
        // so asking for it again can only mean replacing it.
        group.MapPost("/{id:guid}/sources/stream-key", async (
                Guid id,
                HttpContext http,
                IngestCredentialService credentials,
                CancellationToken cancellationToken) =>
            Results.Ok(await credentials.IssueStreamKeyAsync(id, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("IssueStreamKey")
            .WithSummary("Issues or rotates the stream key an external encoder publishes with.");

        group.MapDelete("/{id:guid}/sources/stream-key", async (
                Guid id,
                HttpContext http,
                IngestCredentialService credentials,
                CancellationToken cancellationToken) =>
            {
                await credentials.RevokeStreamKeysAsync(id, http.User.RequireUserId(), cancellationToken);
                return Results.NoContent();
            })
            .WithName("RevokeStreamKey")
            .WithSummary("Revokes the session's encoder stream keys, leaving browser credentials alone.");

        group.MapPost("/{id:guid}/broadcaster-signals", async (
                Guid id,
                [FromBody] BroadcasterSignalRequest request,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            {
                await sessions.RecordBroadcasterSignalAsync(id, http.User.RequireUserId(), request, cancellationToken);
                return Results.Accepted();
            })
            .WithName("RecordBroadcasterSignal")
            .WithSummary("Records a client-side transport event for diagnostics. Does not change session state.");

        // ---------------------------------------------------------------------------------
        // Playback (viewers)
        // ---------------------------------------------------------------------------------

        app.MapGet("/api/v1/live-sessions/{id:guid}/playback", async (
                Guid id,
                HttpContext http,
                LiveSessionService sessions,
                CancellationToken cancellationToken) =>
            Results.Ok(await sessions.GetPlaybackAsync(id, http.User.GetUserId(), cancellationToken)))
            .WithTags("Live Sessions")
            // Anonymous by design: public and unlisted sessions are watchable from a link. The
            // service still refuses playback for private sessions without workspace membership.
            .AllowAnonymous()
            .WithName("GetLiveSessionPlayback")
            .WithSummary("Returns playback URLs for a viewer.");

        return app;
    }
}

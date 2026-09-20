using LiveStream.Api.Security;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources;
using LiveStream.Application.Sources.Contracts;
using LiveStream.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Multi-device endpoints (docs/05-multi-device.md).
///
/// Three audiences, three authentication stories:
/// <list type="bullet">
/// <item>operators, authenticated as workspace users, manage sources;</item>
/// <item>a joining device is anonymous — it has only a pairing code;</item>
/// <item>a paired device authenticates with its own source-scoped token.</item>
/// </list>
/// </summary>
public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        MapOperatorEndpoints(app);
        MapPairingEndpoints(app);
        MapDeviceEndpoints(app);
        return app;
    }

    // -----------------------------------------------------------------------------------------
    // Operator — authenticated workspace users
    // -----------------------------------------------------------------------------------------

    private static void MapOperatorEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/live-sessions/{id:guid}/sources")
            .WithTags("Sources")
            .RequireAuthorization();

        group.MapGet("/", async (
                Guid id,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.ListAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("ListSessionSources")
            .WithSummary("Lists every device and participant contributing to this session.");

        group.MapGet("/{sourceId:guid}/events", async (
                Guid id,
                Guid sourceId,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken,
                [FromQuery] int limit = 50) =>
            Results.Ok(await sources.ListEventsAsync(id, sourceId, http.User.RequireUserId(), limit,
                cancellationToken)))
            .WithName("ListSourceEvents")
            .WithSummary("Returns the audit trail for one source.");

        group.MapPost("/{sourceId:guid}/preview", async (
                Guid id,
                Guid sourceId,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.GetPreviewAsync(id, sourceId, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("GetSourcePreview")
            .WithSummary("Issues a short-lived way for the control room to watch one source.");

        group.MapPost("/invitations", async (
                Guid id,
                [FromBody] InviteSourceRequest request,
                HttpContext http,
                SourceService sources,
                IOptions<JoinLinkOptions> joinOptions,
                CancellationToken cancellationToken) =>
            {
                var invitation = await sources.InviteAsync(id, http.User.RequireUserId(), request,
                    joinOptions.Value.BaseUrl, cancellationToken);

                return Results.Created($"/api/v1/live-sessions/{id}/sources/{invitation.Source.Id}", invitation);
            })
            .RequireRateLimiting(RateLimitPolicies.DeviceManagement)
            .WithName("InviteSource")
            .WithSummary("Creates a single-use pairing code for a new device. The code is returned once.");

        group.MapPatch("/{sourceId:guid}", async (
                Guid id,
                Guid sourceId,
                [FromBody] RenameSourceRequest request,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.RenameAsync(id, sourceId, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DeviceManagement)
            .WithName("RenameSource")
            .WithSummary("Renames a source.");

        group.MapPost("/{sourceId:guid}/program", async (
                Guid id,
                Guid sourceId,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.SetProgramAsync(id, sourceId, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DeviceManagement)
            .WithName("SetSessionProgram")
            .WithSummary("Puts one source on air, returning every source whose program state changed.");

        group.MapDelete("/{sourceId:guid}", async (
                Guid id,
                Guid sourceId,
                HttpContext http,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.RevokeAsync(id, sourceId, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DeviceManagement)
            .WithName("RevokeSource")
            .WithSummary("Withdraws a device immediately and severs its media connection.");
    }

    // -----------------------------------------------------------------------------------------
    // Pairing — anonymous, because the joining device has no account yet
    // -----------------------------------------------------------------------------------------

    private static void MapPairingEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/pairings/claim", async (
                [FromBody] ClaimPairingRequest request,
                SourceService sources,
                CancellationToken cancellationToken) =>
            Results.Ok(await sources.ClaimAsync(request, cancellationToken)))
            .WithTags("Sources")
            .AllowAnonymous()
            // The tightest limit in the system. This is the one endpoint where an unauthenticated
            // caller can guess at a short secret, so the rate limit is load-bearing rather than
            // merely hygienic.
            .RequireRateLimiting(RateLimitPolicies.Pairing)
            .WithName("ClaimPairingCode")
            .WithSummary("Redeems a pairing code and returns a device token. The code is then spent.");
    }

    // -----------------------------------------------------------------------------------------
    // Device — authenticated by its own source-scoped token
    // -----------------------------------------------------------------------------------------

    private static void MapDeviceEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/device")
            .WithTags("Sources")
            .RequireAuthorization(DeviceAuthorizationPolicies.PairedDevice);

        group.MapGet("/session", async (
                SourceService sources,
                IDeviceContext device,
                CancellationToken cancellationToken) =>
            {
                var source = await RequireDeviceAsync(sources, device, cancellationToken);
                return Results.Ok(await sources.DescribeForDeviceAsync(source, cancellationToken));
            })
            .WithName("GetDeviceSession")
            .WithSummary("Returns what this device is allowed to know about the session it joined.");

        group.MapPost("/credentials", async (
                SourceService sources,
                IDeviceContext device,
                IMediaGateway mediaGateway,
                IOptions<LiveSessionOptions> sessionOptions,
                IOptions<IceOptions> iceOptions,
                CancellationToken cancellationToken) =>
            {
                var source = await RequireDeviceAsync(sources, device, cancellationToken);

                var (credential, plaintext, endpoints) = await sources.IssueDeviceCredentialAsync(
                    source, sessionOptions.Value.IngestCredentialLifetime, cancellationToken);

                var iceServers = iceOptions.Value.Servers
                    .Where(server => server.Urls.Count > 0)
                    .Select(server => new IceServerResponse(server.Urls, server.Username, server.Credential))
                    .ToList();

                return Results.Ok(new IngestCredentialResponse(
                    endpoints.IngestProtocol,
                    endpoints.IngestUrl,
                    plaintext,
                    credential.ExpiresAt,
                    sessionOptions.Value.IngestCredentialLifetimeSeconds,
                    iceServers));
            })
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("IssueDeviceCredential")
            .WithSummary("Issues a short-lived credential for this device to publish into its own path.");
    }

    /// <summary>
    /// Resolves the device behind the current request.
    ///
    /// The authentication handler already proved the token, but the source is re-read here so the
    /// request acts on current state — a device revoked between authentication and this call must
    /// not slip through on a stale copy.
    /// </summary>
    private static async Task<Domain.Sources.SessionSource> RequireDeviceAsync(SourceService sources,
        IDeviceContext device, CancellationToken cancellationToken)
    {
        if (device.SourceId is not { } sourceId)
        {
            throw new DomainException(ErrorCodes.DeviceNotAuthorized, "This request is not from a paired device.");
        }

        return await sources.GetActiveSourceAsync(sourceId, cancellationToken)
               ?? throw new DomainException(ErrorCodes.DeviceNotAuthorized,
                   "This device is no longer paired with the session.");
    }
}

/// <summary>
/// Where a joining device should be sent. Used to build the URL a QR code encodes, so it must be
/// the address the phone can actually reach — not the API's own origin.
/// </summary>
public sealed class JoinLinkOptions
{
    public const string SectionName = "JoinLink";

    public string BaseUrl { get; set; } = "http://localhost:3000";
}

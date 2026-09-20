using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LiveStream.Application.Media;
using LiveStream.Application.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LiveStream.Application.Abstractions;

namespace LiveStream.Api.Endpoints;

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";

    /// <summary>
    /// Shared secret the media gateway presents on callbacks. Defence in depth behind network
    /// isolation: these routes must not be reachable from the public internet either.
    /// Supply via environment variable or secret store.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string SharedSecret { get; set; } = string.Empty;
}

/// <summary>
/// Callbacks the media gateway makes into the control plane.
///
/// The auth hook is what keeps streaming credentials server-side: the gateway holds no user
/// database and asks this API to authorize every publish attempt (docs/11-security.md).
/// </summary>
public static class MediaCallbackEndpoints
{
    public const string SecretHeaderName = "X-Internal-Auth";

    public static IEndpointRouteBuilder MapMediaCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/media")
            .WithTags("Internal")
            .AllowAnonymous()
            .ExcludeFromDescription();

        group.MapPost("/auth", async (
                [FromBody] MediaAuthCallback callback,
                HttpContext http,
                IngestCredentialService credentials,
                IOptions<InternalApiOptions> options,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var logger = loggerFactory.CreateLogger("MediaAuthCallback");

                if (!HasValidSecret(http, options.Value))
                {
                    logger.LogWarning("Media auth callback rejected: invalid internal secret");
                    return Results.Unauthorized();
                }

                var action = callback.Action?.ToLowerInvariant() switch
                {
                    "publish" => MediaAccessAction.Publish,
                    "read" or "playback" => MediaAccessAction.Read,
                    // api/metrics/pprof actions are excluded in gateway config; deny anything else.
                    _ => (MediaAccessAction?)null,
                };

                if (action is null)
                {
                    logger.LogWarning("Media auth callback rejected: unsupported action {Action}", callback.Action);
                    return Results.Unauthorized();
                }

                // Gateways carry the token in different places depending on transport, so accept the
                // dedicated token field, the password field, or a `token=`/`pass=` query parameter.
                var token = FirstNonEmpty(
                    callback.Token,
                    callback.Password,
                    ExtractQueryValue(callback.Query, "token"),
                    ExtractQueryValue(callback.Query, "pass"));

                if (string.IsNullOrWhiteSpace(token) && action is MediaAccessAction.Publish)
                {
                    // Which field carried the credential differs by gateway and transport, so record
                    // field *presence* (never values) to make a misconfigured transport diagnosable.
                    logger.LogWarning(
                        "Media publish callback carried no credential. Fields present: user={HasUser} password={HasPassword} token={HasToken} query={HasQuery} protocol={Protocol}",
                        !string.IsNullOrEmpty(callback.User),
                        !string.IsNullOrEmpty(callback.Password),
                        !string.IsNullOrEmpty(callback.Token),
                        !string.IsNullOrEmpty(callback.Query),
                        callback.Protocol);
                }

                var allowed = await credentials.AuthorizeMediaAccessAsync(
                    new MediaAccessRequest(callback.Path ?? string.Empty, token, action.Value, callback.Ip),
                    cancellationToken);

                return allowed ? Results.Ok() : Results.Unauthorized();
            })
            .WithName("MediaAuthCallback");

        group.MapPost("/events", async (
                [FromBody] MediaEventCallback callback,
                HttpContext http,
                IAppDbContext db,
                LiveSessionReconciler reconciler,
                IOptions<InternalApiOptions> options,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var logger = loggerFactory.CreateLogger("MediaEventCallback");

                if (!HasValidSecret(http, options.Value))
                {
                    return Results.Unauthorized();
                }

                var sessionId = await db.LiveSessions
                    .AsNoTracking()
                    .Where(s => s.MediaPathName == callback.Path)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (sessionId is null)
                {
                    logger.LogWarning("Media event for unknown path {MediaPath}", callback.Path);
                    return Results.NoContent();
                }

                logger.LogInformation("Media event {Event} for session {SessionId}", callback.Event, sessionId);

                // Hooks only accelerate reconciliation; the poller reaches the same state on its own,
                // so a lost or duplicated hook cannot corrupt session state.
                await reconciler.ReconcileAsync(sessionId.Value, cancellationToken);
                return Results.NoContent();
            })
            .WithName("MediaEventCallback");

        return app;
    }

    /// <summary>Query-string name for the shared secret, for callers that cannot set headers.</summary>
    public const string SecretQueryName = "s";

    private static bool HasValidSecret(HttpContext http, InternalApiOptions options)
    {
        // Media gateways configure a single callback URL and cannot attach custom headers, so the
        // secret is also accepted as a query parameter. Network isolation remains the primary
        // control: these routes must not be reachable from outside the internal network.
        var presented = http.Request.Headers[SecretHeaderName].FirstOrDefault()
                        ?? http.Request.Query[SecretQueryName].FirstOrDefault();

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // Fixed-time comparison so the secret cannot be recovered by timing the endpoint.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(options.SharedSecret));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? ExtractQueryValue(string? query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (pair.AsSpan(0, separator).SequenceEqual(name))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    /// <summary>Payload the media gateway posts before admitting a publisher or reader.</summary>
    public sealed record MediaAuthCallback
    {
        [JsonPropertyName("user")]
        public string? User { get; init; }

        [JsonPropertyName("password")]
        public string? Password { get; init; }

        [JsonPropertyName("token")]
        public string? Token { get; init; }

        [JsonPropertyName("ip")]
        public string? Ip { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("protocol")]
        public string? Protocol { get; init; }

        [JsonPropertyName("query")]
        public string? Query { get; init; }
    }

    public sealed record MediaEventCallback
    {
        [JsonPropertyName("event")]
        public string Event { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;
    }
}

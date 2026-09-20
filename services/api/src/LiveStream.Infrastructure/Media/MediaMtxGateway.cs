using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LiveStream.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure.Media;

/// <summary>
/// <see cref="IMediaGateway"/> implementation for MediaMTX.
///
/// MediaMTX accepts native browser contribution over WHIP (WebRTC-HTTP Ingestion Protocol), which
/// means the studio needs nothing beyond the standard <c>RTCPeerConnection</c> API — no vendor SDK
/// in the UI. Playback is served as LL-HLS (scale) and WHEP (low latency).
///
/// Publish authorization is delegated back to this control plane through the gateway's HTTP auth
/// hook, so no permanent stream key ever exists in a browser (docs/11-security.md).
/// </summary>
public sealed class MediaMtxGateway(
    HttpClient httpClient,
    IOptions<MediaMtxOptions> options,
    ILogger<MediaMtxGateway> logger) : IMediaGateway
{
    private readonly MediaMtxOptions _options = options.Value;

    public string ProviderName => "mediamtx";

    /// <summary>
    /// MediaMTX serves any path matched by its <c>all_others</c> catch-all configuration, so there is
    /// no per-session resource to create. Provisioning therefore verifies the gateway is reachable,
    /// which is what turns an unreachable media plane into a clear PREPARE failure instead of a
    /// broadcast that silently never starts.
    /// </summary>
    public async Task<MediaPathProvisionResult> ProvisionPathAsync(MediaPathRequest request,
        CancellationToken cancellationToken)
    {
        var endpoints = DescribeEndpoints(request.MediaPathName);

        try
        {
            using var response = await httpClient.GetAsync("v3/config/global/get", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Media gateway health check returned {StatusCode} for session {SessionId}",
                    (int)response.StatusCode, request.LiveSessionId);
                return new MediaPathProvisionResult(false, endpoints,
                    $"Media gateway returned status {(int)response.StatusCode}.");
            }

            logger.LogInformation("Media path provisioned session={SessionId} path={MediaPath} recording={Recording}",
                request.LiveSessionId, request.MediaPathName, request.RecordingEnabled);

            return new MediaPathProvisionResult(true, endpoints);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Media gateway unreachable while provisioning session {SessionId}",
                request.LiveSessionId);
            return new MediaPathProvisionResult(false, endpoints, "Media gateway is unreachable.");
        }
    }

    /// <summary>
    /// Disconnects any publisher still attached to the path. A path that is already gone is a
    /// success, not an error, so stop stays idempotent.
    /// </summary>
    public async Task ReleasePathAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        var state = await GetPathStateAsync(mediaPathName, cancellationToken);
        if (state is null)
        {
            return;
        }

        try
        {
            await KickPublisherAsync(mediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Teardown is best-effort: the session must still reach ENDED.
            logger.LogWarning(ex, "Releasing media path {MediaPath} did not complete", mediaPathName);
        }
    }

    /// <summary>
    /// Severs whatever is publishing to a path, over either transport.
    ///
    /// Both are checked because the two publisher kinds in this system arrive differently: a studio
    /// or a paired device publishes over WebRTC, while the egress relay reads and writes over RTSP.
    /// Kicking only one kind would make revocation work for phones and silently not for anything
    /// else.
    /// </summary>
    public async Task<bool> KickPublisherAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        var kicked = false;

        // Both endpoints return the same {id, path, state} shape, so one reader serves both.
        kicked |= await KickAsync("v3/webrtcsessions", mediaPathName, cancellationToken);
        kicked |= await KickAsync("v3/rtspsessions", mediaPathName, cancellationToken);

        return kicked;
    }

    private async Task<bool> KickAsync(string resource, string mediaPathName,
        CancellationToken cancellationToken)
    {
        var kicked = false;

        try
        {
            var sessions = await httpClient.GetFromJsonAsync<MediaMtxList<MediaMtxWebRtcSession>>(
                $"{resource}/list", cancellationToken);

            var publishers = sessions?.Items?
                .Where(s => string.Equals(s.Path, mediaPathName, StringComparison.Ordinal)
                            && string.Equals(s.State, "publish", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            foreach (var session in publishers)
            {
                using var response = await httpClient.PostAsync(
                    $"{resource}/kick/{Uri.EscapeDataString(session.Id)}", content: null, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    kicked = true;
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    logger.LogWarning("Kicking {Resource} session {SessionId} returned {StatusCode}",
                        resource, session.Id, (int)response.StatusCode);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Listing {Resource} for path {MediaPath} failed", resource, mediaPathName);
        }

        return kicked;
    }

    public async Task<MediaPathState?> GetPathStateAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"v3/paths/get/{Uri.EscapeDataString(mediaPathName)}", cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var path = await response.Content.ReadFromJsonAsync<MediaMtxPath>(cancellationToken);
            if (path is null)
            {
                return null;
            }

            // "ready" means a publisher is attached and its tracks are described; that is exactly the
            // condition under which the control plane is willing to call a session LIVE.
            var publisherConnected = path.Ready && path.Source is not null;

            return new MediaPathState(
                mediaPathName,
                path.Ready,
                publisherConnected,
                path.BytesReceived,
                path.Readers?.Count ?? 0,
                path.Tracks ?? [],
                path.ReadyTime,
                path.Source?.Type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Media path state lookup failed for {MediaPath}", mediaPathName);
            throw;
        }
    }

    public MediaEndpoints DescribeEndpoints(string mediaPathName)
    {
        var webRtcBase = _options.PublicWebRtcUrl.TrimEnd('/');

        // A CDN in front of HLS changes only where viewers fetch segments from. Ingest and WHEP
        // still address the gateway directly.
        var hlsBase = (string.IsNullOrWhiteSpace(_options.PublicHlsCdnUrl)
            ? _options.PublicHlsUrl
            : _options.PublicHlsCdnUrl).TrimEnd('/');

        return new MediaEndpoints(
            IngestProtocol: "WHIP",
            IngestUrl: $"{webRtcBase}/{mediaPathName}/whip",
            HlsPlaybackUrl: $"{hlsBase}/{mediaPathName}/index.m3u8",
            WebRtcPlaybackUrl: $"{webRtcBase}/{mediaPathName}/whep");
    }

    // -----------------------------------------------------------------------------------------
    // Control API payloads (only the fields this control plane relies on)
    // -----------------------------------------------------------------------------------------

    private sealed record MediaMtxPath
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("ready")]
        public bool Ready { get; init; }

        [JsonPropertyName("readyTime")]
        public DateTimeOffset? ReadyTime { get; init; }

        [JsonPropertyName("tracks")]
        public List<string>? Tracks { get; init; }

        [JsonPropertyName("bytesReceived")]
        public long BytesReceived { get; init; }

        [JsonPropertyName("source")]
        public MediaMtxSource? Source { get; init; }

        [JsonPropertyName("readers")]
        public List<MediaMtxReader>? Readers { get; init; }
    }

    private sealed record MediaMtxSource
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
    }

    private sealed record MediaMtxReader
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
    }

    private sealed record MediaMtxWebRtcSession
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;
    }

    private sealed record MediaMtxList<T>
    {
        [JsonPropertyName("items")]
        public List<T>? Items { get; init; }
    }
}

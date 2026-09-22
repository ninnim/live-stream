namespace LiveStream.Application.Abstractions;

/// <summary>
/// The control plane's only view of the media plane (MASTER_BLUEPRINT.md §59, ai/architecture-rules.md).
/// Everything vendor-specific — WHIP negotiation, HLS packaging, recording layout — lives behind this
/// boundary so the media provider can be replaced without touching session logic or the UI.
/// </summary>
public interface IMediaGateway
{
    /// <summary>Identifier of the concrete media provider, recorded on session events for diagnostics.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Ensures the media plane is ready to accept a publisher on <paramref name="request"/>'s path.
    /// Must be idempotent: calling it twice for the same session is not an error.
    /// </summary>
    Task<MediaPathProvisionResult> ProvisionPathAsync(MediaPathRequest request, CancellationToken cancellationToken);

    /// <summary>Tears down the ingest path. Must be idempotent and must not throw when already gone.</summary>
    Task ReleasePathAsync(string mediaPathName, CancellationToken cancellationToken);

    /// <summary>
    /// Current media-plane view of the path, or <c>null</c> when the path is unknown.
    /// Used by the health monitor to reconcile authoritative session state.
    /// </summary>
    Task<MediaPathState?> GetPathStateAsync(string mediaPathName, CancellationToken cancellationToken);

    /// <summary>
    /// Public URLs clients use for this path. Contains no credentials: authorization is carried by
    /// the short-lived token issued separately.
    /// </summary>
    MediaEndpoints DescribeEndpoints(string mediaPathName);

    /// <summary>
    /// Severs whatever is currently publishing to a path.
    ///
    /// Revoking a credential only stops the *next* connection: the media plane authorizes at
    /// connect time, so an established session keeps flowing until something ends it. Immediate
    /// revocation therefore needs this (docs/05-multi-device.md: "Revocation is immediate").
    ///
    /// Returns true when a publisher was found and disconnected. Must not throw when there was
    /// nothing to disconnect.
    /// </summary>
    Task<bool> KickPublisherAsync(string mediaPathName, CancellationToken cancellationToken);
}

public sealed record MediaPathRequest(
    Guid LiveSessionId,
    string MediaPathName,
    bool RecordingEnabled);

public sealed record MediaPathProvisionResult(
    bool Success,
    MediaEndpoints Endpoints,
    string? FailureReason = null);

/// <summary>
/// Client-facing media URLs. <paramref name="IngestUrl"/> is a WHIP endpoint for native browser
/// broadcasting; the playback URLs cover scalable (HLS) and low-latency (WHEP) delivery.
/// </summary>
public sealed record MediaEndpoints(
    string IngestProtocol,
    string IngestUrl,
    string HlsPlaybackUrl,
    string WebRtcPlaybackUrl,
    /// <summary>
    /// Where an external encoder publishes, when external ingest is enabled — null when it is not.
    ///
    /// Null is the default and the safe one: RTMP is an open port that accepts connections from
    /// anywhere, so it exists only where a deployment has turned it on deliberately
    /// (docs/decisions/0022-external-encoder-ingest.md).
    /// </summary>
    string? RtmpIngestUrl = null,
    string? SrtIngestUrl = null);

/// <summary>Point-in-time media-plane observation for one ingest path.</summary>
public sealed record MediaPathState(
    string MediaPathName,
    bool Ready,
    bool PublisherConnected,
    long BytesReceived,
    int ReaderCount,
    IReadOnlyList<string> Tracks,
    DateTimeOffset? ReadySince,
    string? SourceType);

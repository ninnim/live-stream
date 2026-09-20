namespace LiveStream.Application.Abstractions;

/// <summary>
/// The control plane view of the egress relay: the component that pulls the session from the media
/// gateway and republishes it to external RTMP endpoints.
///
/// Mirrors <see cref="IMediaGateway"/> in intent. Everything about how the relay actually moves
/// bytes — FFmpeg, transcoding, process supervision — lives behind this boundary, so session and
/// destination logic never learns what an encoder is (ai/architecture-rules.md).
/// </summary>
public interface IStreamRelay
{
    /// <summary>
    /// Starts republishing one destination. Must be idempotent: calling it twice for the same
    /// destination id restarts nothing and reports the existing relay.
    /// </summary>
    Task<RelayStartResult> StartAsync(RelayStartRequest request, CancellationToken cancellationToken);

    /// <summary>Stops one relay. Must be idempotent and must not throw when it is already gone.</summary>
    Task StopAsync(Guid destinationId, CancellationToken cancellationToken);

    /// <summary>Current relay state, or <c>null</c> when the relay does not know this destination.</summary>
    Task<RelayState?> GetAsync(Guid destinationId, CancellationToken cancellationToken);

    /// <summary>Every relay the service is currently supervising. Used by the reconciler.</summary>
    Task<IReadOnlyList<RelayState>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Instruction to republish one session path to one external endpoint.
///
/// <c>TargetUrl</c> and <c>TargetStreamKey</c> are kept apart all the way to the relay so the key
/// can be excluded from every log line and error message on both sides.
/// </summary>
public sealed record RelayStartRequest(
    Guid DestinationId,
    Guid LiveSessionId,
    string MediaPathName,
    string SourceCredential,
    string TargetUrl,
    string TargetStreamKey,
    string Provider);

public sealed record RelayStartResult(bool Accepted, RelayState? State, string? FailureReason = null);

/// <summary>What the relay is doing right now, from the relay service perspective.</summary>
public sealed record RelayState(
    Guid DestinationId,
    RelayPhase Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ConnectedAt,
    long BytesSent,
    int RestartCount,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset? LastErrorAt);

public enum RelayPhase
{
    /// <summary>Process launched; the platform has not accepted the connection yet.</summary>
    Starting = 0,

    /// <summary>Bytes are flowing to the platform.</summary>
    Connected = 1,

    /// <summary>The process exited. <c>LastErrorCode</c> says whether it is worth retrying.</summary>
    Failed = 2,

    /// <summary>Stopped on request.</summary>
    Stopped = 3,
}

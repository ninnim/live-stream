using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources.Contracts;

namespace LiveStream.Application.Abstractions;

/// <summary>Time source. Injected so lifecycle, expiry, and recovery-window logic is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Outbound realtime channel. Implemented over SignalR in the API host; the application layer
/// stays transport-agnostic (docs/09-api-specification.md SignalR events).
/// </summary>
public interface ILiveSessionNotifier
{
    Task SessionStateChangedAsync(LiveSessionStatusResponse status, CancellationToken cancellationToken);

    Task HealthUpdatedAsync(Guid liveSessionId, LiveSessionHealthResponse health, CancellationToken cancellationToken);

    Task ViewerCountUpdatedAsync(Guid liveSessionId, int viewerCount, CancellationToken cancellationToken);

    Task RecordingStateChangedAsync(Guid liveSessionId, RecordingResponse recording, CancellationToken cancellationToken);

    Task ErrorRaisedAsync(Guid liveSessionId, string errorCode, string message, CancellationToken cancellationToken);

    /// <summary>
    /// One destination changed state (MASTER_BLUEPRINT.md §29 <c>destination.status.changed</c>).
    /// Carried separately from session state so the studio can show a destination failing while the
    /// session badge stays LIVE — the visible form of the isolation guarantee.
    /// </summary>
    Task DestinationStateChangedAsync(DestinationStatusResponse destination, CancellationToken cancellationToken);

    /// <summary>
    /// One contributing source changed state (MASTER_BLUEPRINT.md §29 <c>source.connected</c> /
    /// <c>source.disconnected</c> / <c>source.health.changed</c>). Carried separately from session
    /// state so a phone dropping out of a multi-camera show updates only its own tile.
    /// </summary>
    Task SourceStateChangedAsync(SourceStatusResponse source, CancellationToken cancellationToken);
}

/// <summary>
/// Durable storage for recorded media. Phase 1 ships a local-filesystem implementation that reads
/// what the media gateway wrote; S3-compatible storage is a drop-in replacement
/// (docs/decisions/0005-recording-storage.md).
/// </summary>
public interface IRecordingStore
{
    /// <summary>Storage key/prefix that will hold this session's media.</summary>
    string BuildStorageKey(string mediaPathName);

    /// <summary>
    /// Inspects stored media for a session. Returns <c>null</c> when nothing was written, which is
    /// how "recording enabled but no media captured" is distinguished from a storage failure.
    /// </summary>
    Task<StoredRecording?> InspectAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently removes the media under a storage key. Must be idempotent: retention runs
    /// again after a partial failure, and "already gone" is the outcome it wanted.
    /// Returns the number of bytes removed, for the deletion record.
    /// </summary>
    Task<long> DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public sealed record StoredRecording(long SizeBytes, int SegmentCount, DateTimeOffset? FirstWriteAt, DateTimeOffset? LastWriteAt);

/// <summary>Correlation identifier for the current request, surfaced into logs and session events.</summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;

namespace LiveStream.Application.Sessions.Contracts;

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

public sealed record CreateLiveSessionRequest(
    string Title,
    string? Description,
    string? Visibility,
    bool RecordingEnabled);

public sealed record UpdateLiveSessionRequest(
    string Title,
    string? Description,
    string? Visibility,
    bool RecordingEnabled);

/// <summary>
/// Reported by the broadcaster when its media transport fails or recovers. Advisory only: the
/// server reconciles against the media plane and never trusts client-declared session state.
/// </summary>
public sealed record BroadcasterSignalRequest(
    string Signal,
    string? Detail);

// ---------------------------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------------------------

public sealed record LiveSessionResponse(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string? Description,
    string Status,
    string Visibility,
    bool RecordingEnabled,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    LiveSessionHealthResponse Health,
    PlaybackResponse? Playback);

/// <summary>Compact state payload; also the body of the <c>sessionStateChanged</c> realtime event.</summary>
public sealed record LiveSessionStatusResponse(
    Guid Id,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    bool IsBroadcasting,
    bool IsTerminal,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    string? LastErrorCode,
    string? LastErrorMessage,
    int Version,
    DateTimeOffset ObservedAt);

/// <summary>Body of the <c>healthUpdated</c> realtime event.</summary>
public sealed record LiveSessionHealthResponse(
    string Status,
    bool IngestConnected,
    int? BitrateKbps,
    int? VideoFps,
    int ViewerCount,
    int ReconnectCount,
    DateTimeOffset? LastIngestAt,
    int? RecoveryWindowSecondsRemaining,
    DateTimeOffset? ObservedAt,
    string? LastErrorCode);

/// <summary>
/// Short-lived broadcaster credential. <see cref="Token"/> is returned exactly once and is never
/// persisted in plaintext or logged (docs/11-security.md).
/// </summary>
public sealed record IngestCredentialResponse(
    string Protocol,
    string IngestUrl,
    string Token,
    DateTimeOffset ExpiresAt,
    int ExpiresInSeconds,
    IReadOnlyList<IceServerResponse> IceServers);

/// <summary>
/// What an external encoder needs to publish into a session: where to connect, and the key.
///
/// Two shapes of the same thing, because encoders disagree about how to ask for it. OBS and most
/// phone apps want a server and a stream key as separate fields and join them with a slash;
/// FFmpeg and a few others want one URL. Handing over both removes the step where somebody has to
/// work out which half goes where, which is where this normally goes wrong.
///
/// <see cref="StreamKey"/> is returned exactly once, at issue, and is never retrievable again —
/// only its hash is stored. Losing it means rotating it, which is one button.
/// </summary>
public sealed record StreamKeyResponse(
    string Protocol,
    string ServerUrl,
    string StreamKey,
    string FullUrl,
    string? SrtUrl,
    DateTimeOffset ExpiresAt,
    int ExpiresInSeconds);

/// <summary>An ICE server for the broadcaster to use. Mirrors the browser RTCIceServer shape.</summary>
public sealed record IceServerResponse(
    IReadOnlyList<string> Urls,
    string? Username,
    string? Credential);

public sealed record PlaybackResponse(
    string HlsUrl,
    string WebRtcUrl,
    bool IsLive);

public sealed record RecordingResponse(
    Guid Id,
    string Status,
    string StorageKey,
    string MediaFormat,
    int? DurationSeconds,
    long? SizeBytes,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? FailureReason);

public sealed record LiveSessionEventResponse(
    Guid Id,
    string Type,
    string? FromStatus,
    string? ToStatus,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ---------------------------------------------------------------------------------------------
// Mapping
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Domain-to-contract projection. Centralised so no endpoint can accidentally leak a field that
/// is not part of the published contract (for example credential hashes).
/// </summary>
public static class LiveSessionMapper
{
    public static LiveSessionResponse ToResponse(LiveSession session, PlaybackResponse? playback) => new(
        session.Id,
        session.WorkspaceId,
        session.Title,
        session.Description,
        session.Status.ToString().ToUpperInvariant(),
        session.Visibility.ToString().ToUpperInvariant(),
        session.RecordingEnabled,
        session.StartedAt,
        session.EndedAt,
        session.LastErrorCode,
        session.LastErrorMessage,
        session.CreatedAt,
        session.UpdatedAt,
        session.Version,
        ToHealthResponse(session.Health, TimeSpan.Zero, session.StartedAt is not null ? DateTimeOffset.UtcNow : null),
        playback);

    public static LiveSessionStatusResponse ToStatusResponse(LiveSession session, DateTimeOffset now)
    {
        var duration = session.StartedAt is null
            ? (int?)null
            : (int)Math.Max(0, ((session.EndedAt ?? now) - session.StartedAt.Value).TotalSeconds);

        var allowed = LiveSessionStateMachine.AllowedTargets(session.Status)
            .Select(s => s.ToString().ToUpperInvariant())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new LiveSessionStatusResponse(
            session.Id,
            session.Status.ToString().ToUpperInvariant(),
            allowed,
            LiveSessionStateMachine.IsBroadcasting(session.Status),
            LiveSessionStateMachine.IsTerminal(session.Status),
            session.StartedAt,
            session.EndedAt,
            duration,
            session.LastErrorCode,
            session.LastErrorMessage,
            session.Version,
            now);
    }

    public static LiveSessionHealthResponse ToHealthResponse(
        LiveSessionHealth health,
        TimeSpan recoveryWindow,
        DateTimeOffset? now)
    {
        int? remaining = null;
        if (health.RecoveryStartedAt is { } startedAt && now is { } current && recoveryWindow > TimeSpan.Zero)
        {
            var left = (int)Math.Ceiling((startedAt + recoveryWindow - current).TotalSeconds);
            remaining = Math.Max(0, left);
        }

        return new LiveSessionHealthResponse(
            health.Status.ToString().ToUpperInvariant(),
            health.IngestConnected,
            health.BitrateKbps,
            health.VideoFps,
            health.ViewerCount,
            health.ReconnectCount,
            health.LastIngestAt,
            remaining,
            health.ObservedAt,
            health.LastErrorCode);
    }

    public static RecordingResponse ToResponse(Recording recording) => new(
        recording.Id,
        recording.Status.ToString().ToUpperInvariant(),
        recording.StorageKey,
        recording.MediaFormat,
        recording.DurationSeconds,
        recording.SizeBytes,
        recording.StartedAt,
        recording.EndedAt,
        recording.FailureReason);

    public static LiveSessionEventResponse ToResponse(LiveSessionEvent sessionEvent) => new(
        sessionEvent.Id,
        sessionEvent.Type.ToString(),
        sessionEvent.FromStatus?.ToString().ToUpperInvariant(),
        sessionEvent.ToStatus?.ToString().ToUpperInvariant(),
        sessionEvent.ErrorCode,
        sessionEvent.Detail,
        sessionEvent.CreatedAt);
}

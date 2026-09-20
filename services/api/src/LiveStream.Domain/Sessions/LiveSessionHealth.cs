namespace LiveStream.Domain.Sessions;

/// <summary>
/// Latest known media health for a session. One row per session (owned 1:1), continuously
/// overwritten by the health monitor. High-frequency history belongs in a telemetry store,
/// not this transactional table (docs/02 principle 7).
/// </summary>
public class LiveSessionHealth
{
    public Guid LiveSessionId { get; set; }

    public StreamHealthStatus Status { get; set; } = StreamHealthStatus.Unknown;

    /// <summary>True when the media gateway reports an active publisher on the ingest path.</summary>
    public bool IngestConnected { get; set; }

    public int? BitrateKbps { get; set; }

    public int? VideoFps { get; set; }

    public int ViewerCount { get; set; }

    public long BytesReceived { get; set; }

    /// <summary>Count of reconnect attempts observed during this session.</summary>
    public int ReconnectCount { get; set; }

    public DateTimeOffset? LastIngestAt { get; set; }

    /// <summary>When the session entered RECONNECTING, used to bound the recovery window.</summary>
    public DateTimeOffset? RecoveryStartedAt { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }

    public string? LastErrorCode { get; set; }

    public LiveSession? LiveSession { get; set; }
}

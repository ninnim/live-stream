namespace LiveStream.Domain.Sessions;

/// <summary>Auditable events recorded against a Live Session (docs/12-observability-and-reliability.md).</summary>
public enum LiveSessionEventType
{
    Created = 0,
    Prepared = 1,
    StateChanged = 2,
    Started = 3,
    Stopped = 4,
    Failed = 5,
    IngestConnected = 6,
    IngestDisconnected = 7,
    ReconnectAttempted = 8,
    ReconnectSucceeded = 9,
    ReconnectFailed = 10,
    HealthChanged = 11,
    CredentialIssued = 12,
    CredentialRevoked = 13,
    RecordingStateChanged = 14,
    ClientError = 15,
}

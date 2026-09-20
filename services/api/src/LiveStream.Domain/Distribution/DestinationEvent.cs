namespace LiveStream.Domain.Distribution;

/// <summary>Auditable events recorded against one destination (docs/12-observability-and-reliability.md).</summary>
public enum DestinationEventType
{
    Created = 0,
    Updated = 1,
    Removed = 2,
    StateChanged = 3,
    TargetResolved = 4,
    Connected = 5,
    Disconnected = 6,
    RetryScheduled = 7,
    RetryExhausted = 8,
    AuthFailed = 9,
    CredentialRotated = 10,
    Enabled = 11,
    Disabled = 12,
}

/// <summary>
/// One entry in a destination's audit trail.
///
/// <see cref="Id"/> is assigned by the persistence layer on insert. Deliberately not pre-populated:
/// events are appended to the parent's collection, and change tracking treats a child discovered
/// with a key already set as an existing row to UPDATE rather than a new row to INSERT.
/// </summary>
public class DestinationEvent
{
    public Guid Id { get; set; }

    public Guid StreamDestinationId { get; set; }

    public DestinationEventType Type { get; set; }

    public DestinationStatus? FromStatus { get; set; }

    public DestinationStatus? ToStatus { get; set; }

    /// <summary>Normalized internal error code. Provider-specific codes never reach this field.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Human-readable context. Must never contain a stream key, access token, or any part of one —
    /// destinations are the one place in the system where a long-lived platform secret is handled.
    /// </summary>
    public string? Detail { get; set; }

    public string? CorrelationId { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public StreamDestination? StreamDestination { get; set; }
}

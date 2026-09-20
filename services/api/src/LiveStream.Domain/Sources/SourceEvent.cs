namespace LiveStream.Domain.Sources;

/// <summary>
/// One entry in a source's audit trail.
///
/// <see cref="Id"/> is assigned by the persistence layer on insert. Deliberately not pre-populated:
/// events are appended to the parent's collection, and change tracking treats a child discovered
/// with a key already set as an existing row to UPDATE rather than a new row to INSERT.
/// </summary>
public class SourceEvent
{
    public Guid Id { get; set; }

    public Guid SessionSourceId { get; set; }

    public SourceEventType Type { get; set; }

    public SourceStatus? FromStatus { get; set; }

    public SourceStatus? ToStatus { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>
    /// Human-readable context. Must never contain a pairing code or a device token — those are the
    /// two secrets this aggregate handles, and an audit trail is exactly the kind of place a
    /// credential quietly ends up.
    /// </summary>
    public string? Detail { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>The workspace user who acted, when there was one. Null for device-initiated events.</summary>
    public Guid? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public SessionSource? SessionSource { get; set; }
}

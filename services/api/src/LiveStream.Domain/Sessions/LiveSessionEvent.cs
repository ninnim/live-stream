namespace LiveStream.Domain.Sessions;

/// <summary>
/// An append-only record of something that happened to a Live Session. Every state transition
/// produces one, satisfying "All state transitions must be logged".
/// </summary>
public class LiveSessionEvent
{
    /// <summary>
    /// Assigned by the persistence layer on insert. Deliberately not pre-populated: events are
    /// appended to the aggregate's collection, and change tracking treats a child discovered with a
    /// key already set as an existing row to UPDATE rather than a new row to INSERT.
    /// </summary>
    public Guid Id { get; set; }

    public Guid LiveSessionId { get; set; }

    public LiveSessionEventType Type { get; set; }

    public LiveSessionStatus? FromStatus { get; set; }

    public LiveSessionStatus? ToStatus { get; set; }

    /// <summary>Stable error code when the event represents a failure; otherwise null.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Operator-facing detail. Must never contain secrets or credentials.</summary>
    public string? Detail { get; set; }

    /// <summary>Request/trace correlation identifier, when the event originated from a request.</summary>
    public string? CorrelationId { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public LiveSession? LiveSession { get; set; }
}

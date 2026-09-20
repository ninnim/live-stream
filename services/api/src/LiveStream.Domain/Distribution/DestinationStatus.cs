namespace LiveStream.Domain.Distribution;

/// <summary>
/// Lifecycle of one destination within one Live Session.
///
/// Deliberately separate from <see cref="LiveStream.Domain.Sessions.LiveSessionStatus"/>: a
/// destination failing must never move the session, which is the central requirement of
/// docs/06-multi-platform-distribution.md ("Do not stop the whole session because Facebook is
/// unavailable").
/// </summary>
public enum DestinationStatus
{
    /// <summary>Configured but not part of an active broadcast.</summary>
    Idle = 0,

    /// <summary>Resolving the RTMP target — validating a stream key, or creating a broadcast via the provider API.</summary>
    Preparing = 1,

    /// <summary>Target resolved; the relay is opening the connection to the platform.</summary>
    Connecting = 2,

    /// <summary>Media is flowing to the platform.</summary>
    Live = 3,

    /// <summary>Connection dropped inside the retry budget. Backing off before the next attempt.</summary>
    Retrying = 4,

    /// <summary>Operator asked to stop, or the session ended. Draining the relay.</summary>
    Stopping = 5,

    /// <summary>Clean stop. The destination can be started again in a later session.</summary>
    Stopped = 6,

    /// <summary>Unrecoverable for this run: authentication rejected, or the retry budget is spent.</summary>
    Error = 7,

    /// <summary>Operator turned this destination off. It is skipped when the session starts.</summary>
    Disabled = 8,
}

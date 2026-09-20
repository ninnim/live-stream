namespace LiveStream.Domain.Sessions;

/// <summary>
/// Authoritative Live Session lifecycle states.
/// Reconciles MASTER_BLUEPRINT.md §12 (STARTING/DEGRADED) with docs/02-system-architecture.md
/// (PREPARING); see docs/decisions/0002-live-session-state-machine.md.
/// </summary>
public enum LiveSessionStatus
{
    /// <summary>Created but not yet validated for broadcast.</summary>
    Draft = 0,

    /// <summary>Server is allocating media resources (ingest path, recording target).</summary>
    Preparing = 1,

    /// <summary>Media resources allocated; the broadcaster may request ingest credentials.</summary>
    Ready = 2,

    /// <summary>Start requested; waiting for the media plane to confirm ingest.</summary>
    Starting = 3,

    /// <summary>Ingest confirmed and the stream is publishable to viewers.</summary>
    Live = 4,

    /// <summary>Live but below the healthy quality threshold. Still broadcasting.</summary>
    Degraded = 5,

    /// <summary>Ingest lost inside the bounded recovery window. Session is preserved.</summary>
    Reconnecting = 6,

    /// <summary>Stop requested; finalizing media and recording.</summary>
    Stopping = 7,

    /// <summary>Terminal success state.</summary>
    Ended = 8,

    /// <summary>Terminal failure state.</summary>
    Failed = 9,
}

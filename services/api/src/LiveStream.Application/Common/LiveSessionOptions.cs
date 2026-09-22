using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Common;

/// <summary>
/// Tunables for the Live Session lifecycle. Bound from the <c>LiveSessions</c> configuration
/// section and validated on startup so a misconfigured deployment fails fast rather than
/// misbehaving during a broadcast.
/// </summary>
public sealed class LiveSessionOptions
{
    public const string SectionName = "LiveSessions";

    /// <summary>Lifetime of a broadcaster ingest credential. Short by design; the client re-requests one on reconnect.</summary>
    [Range(30, 3600)]
    public int IngestCredentialLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// Lifetime of an external encoder's stream key.
    /// </summary>
    /// <remarks>
    /// Twelve hours by default — two orders of magnitude longer than a browser credential, and
    /// that is the point rather than an oversight. A key is typed into an encoder by hand and
    /// re-presented on every reconnect, so anything short enough to be "safe" would drop the show
    /// mid-broadcast and could not be renewed without the person stopping to retype it.
    ///
    /// The compensating controls are the ones that actually fit the threat: the key is bound to one
    /// session's path, it is rotatable and revocable from the studio at any moment, issuing a new
    /// one invalidates the old immediately, and every key dies with the session whatever its
    /// remaining lifetime says. See docs/decisions/0022-external-encoder-ingest.md.
    ///
    /// The ceiling is one week. A key that outlives the show it was made for is a stream key in the
    /// sense this platform set out not to have.
    /// </remarks>
    [Range(300, 604_800)]
    public int StreamKeyLifetimeSeconds { get; set; } = 43_200;

    /// <summary>
    /// How long a LIVE session may stay in RECONNECTING before it is declared FAILED.
    /// A temporary network outage must never be converted straight to ENDED (MASTER_BLUEPRINT.md §11.3).
    /// </summary>
    [Range(10, 1800)]
    public int RecoveryWindowSeconds { get; set; } = 120;

    /// <summary>
    /// Grace period after START during which no ingest is expected yet, before the session is
    /// considered a failed start.
    /// </summary>
    [Range(5, 600)]
    public int StartIngestTimeoutSeconds { get; set; } = 60;

    /// <summary>Interval at which the health monitor reconciles session state against the media plane.</summary>
    [Range(1, 60)]
    public int HealthPollIntervalSeconds { get; set; } = 3;

    /// <summary>Sustained bitrate at or above this is reported as GOOD.</summary>
    [Range(1, 100000)]
    public int HealthyBitrateKbps { get; set; } = 1200;

    /// <summary>Sustained bitrate below this is reported as POOR and the session is marked DEGRADED.</summary>
    [Range(1, 100000)]
    public int PoorBitrateKbps { get; set; } = 400;

    /// <summary>Maximum concurrent non-terminal sessions per workspace (abuse control, docs/11-security.md).</summary>
    [Range(1, 100)]
    public int MaxConcurrentSessionsPerWorkspace { get; set; } = 3;

    public TimeSpan IngestCredentialLifetime => TimeSpan.FromSeconds(IngestCredentialLifetimeSeconds);

    public TimeSpan StreamKeyLifetime => TimeSpan.FromSeconds(StreamKeyLifetimeSeconds);

    public TimeSpan RecoveryWindow => TimeSpan.FromSeconds(RecoveryWindowSeconds);

    public TimeSpan StartIngestTimeout => TimeSpan.FromSeconds(StartIngestTimeoutSeconds);

    public TimeSpan HealthPollInterval => TimeSpan.FromSeconds(HealthPollIntervalSeconds);
}

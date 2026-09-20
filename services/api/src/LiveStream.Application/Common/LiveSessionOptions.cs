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

    public TimeSpan RecoveryWindow => TimeSpan.FromSeconds(RecoveryWindowSeconds);

    public TimeSpan StartIngestTimeout => TimeSpan.FromSeconds(StartIngestTimeoutSeconds);

    public TimeSpan HealthPollInterval => TimeSpan.FromSeconds(HealthPollIntervalSeconds);
}

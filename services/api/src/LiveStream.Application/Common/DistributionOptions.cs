using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Common;

/// <summary>
/// Tunables for multi-platform distribution. Bound from the <c>Distribution</c> configuration
/// section and validated on startup.
/// </summary>
public sealed class DistributionOptions
{
    public const string SectionName = "Distribution";

    /// <summary>
    /// Maximum destinations per session. An abuse control as much as a resource one: each
    /// destination costs an encoder pipeline (docs/11-security.md, abuse prevention).
    /// </summary>
    [Range(1, 20)]
    public int MaxDestinationsPerSession { get; set; } = 5;

    /// <summary>
    /// How many times a destination reconnects before it is left in ERROR.
    /// Bounded on purpose: an endlessly retrying relay against a permanently dead endpoint is a way
    /// to get an account rate-limited.
    /// </summary>
    [Range(0, 20)]
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>First retry delay. Subsequent delays double up to <see cref="MaxRetryDelaySeconds"/>.</summary>
    [Range(1, 300)]
    public int InitialRetryDelaySeconds { get; set; } = 5;

    [Range(1, 3600)]
    public int MaxRetryDelaySeconds { get; set; } = 120;

    /// <summary>
    /// How long a destination may sit in CONNECTING before it is treated as a failed attempt.
    /// Platforms accept an RTMP handshake quickly or not at all.
    /// </summary>
    [Range(5, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 45;

    /// <summary>Interval at which destination state is reconciled against the relay.</summary>
    [Range(1, 60)]
    public int ReconcileIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Lifetime of the read credential issued to the relay so it can pull the session from the
    /// media gateway. Longer than a browser ingest credential because the relay holds one open for
    /// the whole broadcast, and shorter than a typical stream so a leak expires on its own.
    /// </summary>
    [Range(60, 86400)]
    public int RelaySourceCredentialLifetimeSeconds { get; set; } = 21600;

    /// <summary>
    /// Refresh an OAuth access token when it is within this window of expiring, rather than waiting
    /// for a 401 mid-broadcast.
    /// </summary>
    [Range(30, 3600)]
    public int TokenRefreshSkewSeconds { get; set; } = 300;

    /// <summary>How long an OAuth <c>state</c> value stays valid between redirect and callback.</summary>
    [Range(60, 3600)]
    public int OAuthStateLifetimeSeconds { get; set; } = 600;

    public TimeSpan InitialRetryDelay => TimeSpan.FromSeconds(InitialRetryDelaySeconds);

    public TimeSpan MaxRetryDelay => TimeSpan.FromSeconds(MaxRetryDelaySeconds);

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(ConnectTimeoutSeconds);

    public TimeSpan ReconcileInterval => TimeSpan.FromSeconds(ReconcileIntervalSeconds);

    public TimeSpan RelaySourceCredentialLifetime => TimeSpan.FromSeconds(RelaySourceCredentialLifetimeSeconds);

    public TimeSpan TokenRefreshSkew => TimeSpan.FromSeconds(TokenRefreshSkewSeconds);

    public TimeSpan OAuthStateLifetime => TimeSpan.FromSeconds(OAuthStateLifetimeSeconds);

    /// <summary>
    /// Exponential backoff with a ceiling. Attempt 1 waits the initial delay, each subsequent
    /// attempt doubles it.
    /// </summary>
    public TimeSpan RetryDelayForAttempt(int attempt)
    {
        if (attempt <= 1)
        {
            return InitialRetryDelay;
        }

        // Shifting past 30 would overflow; the ceiling makes anything beyond it identical anyway.
        var exponent = Math.Min(attempt - 1, 30);
        var seconds = (double)InitialRetryDelaySeconds * Math.Pow(2, exponent);

        return seconds >= MaxRetryDelaySeconds ? MaxRetryDelay : TimeSpan.FromSeconds(seconds);
    }
}

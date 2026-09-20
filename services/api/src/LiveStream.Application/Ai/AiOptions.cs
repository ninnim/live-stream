using LiveStream.Domain.Ai;

namespace LiveStream.Application.Ai;

/// <summary>
/// AI configuration.
///
/// Every feature is separately switchable, which the phase requires
/// (implementation/phase-6-ai-live-operations.md: AI features must be "independently disableable").
/// It is not a formality: these have different costs and different consequences for being wrong, so
/// an operator needs to be able to run one and not another.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// The master switch. Off by default: a deployment should have to choose to send its session
    /// data to a model, rather than discover that it already had.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The API key. Absent means no provider, which the API reports honestly rather than queuing
    /// work that cannot run.
    /// </summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    /// <summary>Per-feature switches, keyed by <see cref="AiJobKind"/> name.</summary>
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(AiJobKind.SessionRecap)] = true,
        [nameof(AiJobKind.StreamQualityReview)] = true,
        [nameof(AiJobKind.Chapters)] = true,
    };

    /// <summary>
    /// How many timeline entries a prompt may carry.
    ///
    /// A cap rather than a truncation of the underlying data: a long session's timeline is
    /// summarised down to this many entries by sampling, and the job records the range it actually
    /// covered so the answer stays traceable.
    /// </summary>
    public int MaxTimelineEntries { get; set; } = 400;

    /// <summary>How often the worker looks for queued jobs.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Jobs run per poll.
    ///
    /// One at a time by default. These are minutes-long, money-spending calls, and a burst of them
    /// competing for the same connection pool is a worse failure than a slow queue.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    public bool IsFeatureEnabled(AiJobKind kind) =>
        Enabled && (!Features.TryGetValue(kind.ToString(), out var enabled) || enabled);
}

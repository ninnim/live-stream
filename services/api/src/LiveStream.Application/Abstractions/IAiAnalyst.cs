using LiveStream.Domain.Ai;

namespace LiveStream.Application.Abstractions;

/// <summary>One thing that happened during a session, as the model is shown it.</summary>
/// <param name="OffsetSeconds">Seconds from the start of the broadcast, or null before it began.</param>
public sealed record AiTimelineEntry(
    DateTimeOffset At,
    double? OffsetSeconds,
    string Category,
    string Type,
    string? Detail);

/// <summary>
/// Everything the model is given. Deliberately a value: no entity, no navigation property, nothing
/// that could pull the rest of the database into a prompt by accident.
/// </summary>
public sealed record AiAnalysisRequest(
    AiJobKind Kind,
    string SessionTitle,
    string? SessionDescription,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    double? DurationSeconds,
    IReadOnlyList<AiTimelineEntry> Timeline);

/// <summary>
/// What came back. Tokens and model are carried so the job row can answer "what produced this, and
/// what did it cost" without a second lookup.
/// </summary>
public sealed record AiAnalysisResult(
    string Summary,
    string ResultJson,
    string ModelId,
    int InputTokens,
    int OutputTokens);

/// <summary>
/// A failure from the AI provider, carrying whether trying again could plausibly help.
///
/// The distinction is made here rather than by the caller inspecting exception types, because only
/// the adapter knows what its provider's errors mean.
/// </summary>
public sealed class AiProviderException(string errorCode, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public string ErrorCode { get; } = errorCode;

    public bool Retryable { get; } = retryable;
}

/// <summary>
/// The seam between the platform and whatever model runs behind it.
///
/// It exists so that AI is a leaf of the system rather than a dependency of it: nothing on the
/// broadcast path references this interface, and an implementation that throws on every call costs
/// the platform a failed job and nothing else
/// (implementation/phase-6-ai-live-operations.md: "Core streaming remains functional when AI
/// services are unavailable").
/// </summary>
public interface IAiAnalyst
{
    /// <summary>False when no provider is configured, so the API can say so instead of queuing work
    /// that is certain to fail.</summary>
    bool IsConfigured { get; }

    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Stands in when no AI provider is configured.
///
/// Refuses rather than pretending: a stub that returned plausible text would be a fabricated
/// analysis presented as a real one, and an operator would act on it.
/// </summary>
public sealed class UnconfiguredAiAnalyst : IAiAnalyst
{
    public bool IsConfigured => false;

    public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken) =>
        throw new AiProviderException("AI_NOT_CONFIGURED",
            "No AI provider is configured for this deployment.", retryable: false);
}

using LiveStream.Domain.Ai;

namespace LiveStream.Application.Ai.Contracts;

public sealed record RequestAiJobRequest(string Kind);

/// <summary>
/// One AI job, as the control room sees it.
///
/// Carries the whole lifecycle — attempts, timings, tokens, cost, failure — because "observable"
/// in the phase's acceptance criteria has to mean something an operator can read, not something a
/// developer can grep for.
/// </summary>
public sealed record AiJobResponse(
    Guid Id,
    Guid LiveSessionId,
    string Kind,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NextAttemptAt,
    string? Summary,
    string? ResultJson,
    string? ErrorCode,
    string? ErrorMessage,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCostUsd,
    DateTimeOffset? SourceRangeStart,
    DateTimeOffset? SourceRangeEnd,
    DateTimeOffset UpdatedAt);

/// <summary>What this deployment can actually do, so the UI never offers a button that cannot work.</summary>
public sealed record AiCapabilitiesResponse(bool Configured, IReadOnlyList<AiFeatureResponse> Features);

public sealed record AiFeatureResponse(string Kind, string DisplayName, string Description, bool Enabled);

public static class AiMapper
{
    public static AiJobResponse ToResponse(AiJob job) => new(
        job.Id,
        job.LiveSessionId,
        job.Kind.ToString(),
        job.Status.ToString(),
        job.AttemptCount,
        AiJob.MaxAttempts,
        job.RequestedAt,
        job.StartedAt,
        job.CompletedAt,
        job.Status == AiJobStatus.Queued ? job.NextAttemptAt : null,
        job.Summary,
        job.ResultJson,
        job.ErrorCode,
        job.ErrorMessage,
        job.ModelId,
        job.InputTokens,
        job.OutputTokens,
        AiPricing.EstimateUsd(job.ModelId, job.InputTokens, job.OutputTokens),
        job.SourceRangeStart,
        job.SourceRangeEnd,
        job.UpdatedAt);

    public static AiFeatureResponse Describe(AiJobKind kind, bool enabled) => kind switch
    {
        AiJobKind.SessionRecap => new(kind.ToString(), "Session recap",
            "A plain-English account of what happened during the broadcast.", enabled),
        AiJobKind.StreamQualityReview => new(kind.ToString(), "Stream quality review",
            "What went wrong technically, and what to change before the next show.", enabled),
        AiJobKind.Chapters => new(kind.ToString(), "Chapters",
            "Chapter markers for the recording, taken from the session's own timeline.", enabled),
        _ => new(kind.ToString(), kind.ToString(), string.Empty, enabled),
    };
}

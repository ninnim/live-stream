using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Ai;

/// <summary>
/// What an AI job is being asked to produce.
///
/// Each kind is independently disableable in configuration, because they have different costs and
/// different appetites for being wrong (implementation/phase-6-ai-live-operations.md).
/// </summary>
public enum AiJobKind
{
    /// <summary>A plain-English account of what happened during the broadcast.</summary>
    SessionRecap = 0,

    /// <summary>What went wrong technically, and what to change before the next show.</summary>
    StreamQualityReview = 1,

    /// <summary>Chapter markers for the recording, derived from the session's own timeline.</summary>
    Chapters = 2,
}

public enum AiJobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// One unit of AI work, run away from the broadcast path.
///
/// The phase's acceptance criteria are the design: AI must be **asynchronous**, **observable**,
/// **permissioned** and **independently disableable**, and core streaming must keep working when
/// the AI service does not. So nothing here is ever called from a request that a broadcaster is
/// waiting on, and nothing on the media path holds a reference to any of it.
///
/// The job row is the observability surface: every attempt, timing, token count and failure is on
/// it, so "what did the AI do, when, at what cost, and why did it fail" is one query rather than a
/// log search.
/// </summary>
public class AiJob
{
    /// <summary>
    /// Attempts before a job is abandoned.
    ///
    /// Low on purpose. Each attempt costs real money, and a job that has failed three times is
    /// failing for a reason a fourth attempt will not discover.
    /// </summary>
    public const int MaxAttempts = 3;

    private AiJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid LiveSessionId { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public AiJobKind Kind { get; private set; }

    public AiJobStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>When this job may next be picked up. Set on every retry, and drives the backoff.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>A one-line answer, for a list view that should not have to parse JSON.</summary>
    public string? Summary { get; private set; }

    /// <summary>The full structured result. Shape depends on <see cref="Kind"/>.</summary>
    public string? ResultJson { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Which model produced the result. Recorded so an answer can be traced to its source.</summary>
    public string? ModelId { get; private set; }

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    /// <summary>
    /// The window of session activity the answer was derived from.
    ///
    /// docs/13-ai-features.md: "AI output must be traceable to the session/time range used." Without
    /// this, a recap is an assertion nobody can check.
    /// </summary>
    public DateTimeOffset? SourceRangeStart { get; private set; }

    public DateTimeOffset? SourceRangeEnd { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency: the worker and an operator can race this row.</summary>
    public int Version { get; private set; }

    public LiveSession? LiveSession { get; set; }

    public static AiJob Queue(Guid liveSessionId, Guid workspaceId, AiJobKind kind, Guid requestedByUserId,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown AI job kind.");
        }

        return new AiJob
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            WorkspaceId = workspaceId,
            Kind = kind,
            Status = AiJobStatus.Queued,
            RequestedByUserId = requestedByUserId,
            RequestedAt = now,
            NextAttemptAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>True while the job may still produce a result, and so should not be duplicated.</summary>
    public bool IsActive => Status is AiJobStatus.Queued or AiJobStatus.Running;

    public void Start(DateTimeOffset now)
    {
        if (Status is not AiJobStatus.Queued)
        {
            throw new DomainException(ErrorCodes.InvalidStateTransition,
                $"An AI job can only start from Queued, not {Status}.");
        }

        Status = AiJobStatus.Running;
        AttemptCount += 1;
        StartedAt ??= now;
        UpdatedAt = now;
    }

    public void Succeed(string summary, string resultJson, string modelId, int inputTokens, int outputTokens,
        DateTimeOffset? rangeStart, DateTimeOffset? rangeEnd, DateTimeOffset now)
    {
        Status = AiJobStatus.Succeeded;
        Summary = Truncate(summary, 500);
        ResultJson = resultJson;
        ModelId = modelId;
        InputTokens = Math.Max(0, inputTokens);
        OutputTokens = Math.Max(0, outputTokens);
        SourceRangeStart = rangeStart;
        SourceRangeEnd = rangeEnd;
        ErrorCode = null;
        ErrorMessage = null;
        CompletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records a failed attempt.
    ///
    /// A retryable failure goes back to the queue with a delay; anything else is final. The
    /// distinction is the caller's to make, because only the caller knows whether the provider said
    /// "try later" or "never".
    /// </summary>
    public void Fail(string errorCode, string message, bool retryable, DateTimeOffset now)
    {
        ErrorCode = errorCode;
        ErrorMessage = Truncate(message, 1000);
        UpdatedAt = now;

        if (retryable && AttemptCount < MaxAttempts)
        {
            Status = AiJobStatus.Queued;
            NextAttemptAt = now + RetryDelay(AttemptCount);
            return;
        }

        Status = AiJobStatus.Failed;
        CompletedAt = now;
    }

    /// <summary>Withdraws a job that has not started. A running job is left to finish or fail.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is not AiJobStatus.Queued)
        {
            throw new DomainException(ErrorCodes.InvalidStateTransition,
                $"Only a queued AI job can be cancelled, not one that is {Status}.");
        }

        Status = AiJobStatus.Cancelled;
        CompletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Backoff between attempts: 30 s, then 2 min, then 8 min.
    ///
    /// Far longer than the media path's, deliberately. Nothing is waiting on this, and the failures
    /// worth retrying — rate limits and provider outages — are measured in minutes, not seconds.
    /// </summary>
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(30 * Math.Pow(4, Math.Max(0, attempt - 1)));

    private static string Truncate(string value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}

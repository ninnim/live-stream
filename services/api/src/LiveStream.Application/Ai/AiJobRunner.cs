using LiveStream.Application.Abstractions;
using LiveStream.Domain.Ai;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Ai;

/// <summary>
/// Runs queued AI jobs, one pass at a time.
///
/// Everything about this class exists to keep AI failure away from the broadcast. It runs on its
/// own loop, it holds no reference to anything on the media path, and a provider that is down
/// produces failed jobs and nothing else. A job that throws is recorded and retried; a pass that
/// throws is logged and the next one runs.
/// </summary>
public sealed class AiJobRunner(
    IAppDbContext db,
    IAiAnalyst analyst,
    IClock clock,
    IOptions<AiOptions> options,
    ILogger<AiJobRunner> logger)
{
    private readonly AiOptions _options = options.Value;

    /// <summary>Runs at most <see cref="AiOptions.MaxConcurrentJobs"/> jobs. Returns how many ran.</summary>
    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !analyst.IsConfigured)
        {
            return 0;
        }

        var now = clock.UtcNow;

        var due = await db.AiJobs
            .Where(job => job.Status == AiJobStatus.Queued && job.NextAttemptAt <= now)
            .OrderBy(job => job.RequestedAt)
            .Take(Math.Max(1, _options.MaxConcurrentJobs))
            .ToListAsync(cancellationToken);

        var ran = 0;

        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-checked per job rather than once at the top: a feature turned off while work was
            // queued should stop, not drain.
            if (!_options.IsFeatureEnabled(job.Kind))
            {
                job.Fail("AI_FEATURE_DISABLED", $"The {job.Kind} feature is turned off.", retryable: false,
                    clock.UtcNow);
                continue;
            }

            await RunOneAsync(job, cancellationToken);
            ran += 1;
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return ran;
    }

    private async Task RunOneAsync(AiJob job, CancellationToken cancellationToken)
    {
        job.Start(clock.UtcNow);

        // Committed before the call so a crash mid-request leaves a Running job rather than a
        // Queued one that gets picked up again and billed twice.
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var request = await BuildRequestAsync(job, cancellationToken);
            var result = await analyst.AnalyzeAsync(request, cancellationToken);

            var range = request.Timeline.Count > 0
                ? (request.Timeline[0].At, request.Timeline[^1].At)
                : ((DateTimeOffset?)null, (DateTimeOffset?)null);

            job.Succeed(result.Summary, result.ResultJson, result.ModelId, result.InputTokens,
                result.OutputTokens, range.Item1, range.Item2, clock.UtcNow);

            logger.LogInformation(
                "AI job succeeded job={JobId} kind={Kind} attempt={Attempt} model={Model} inputTokens={InputTokens} outputTokens={OutputTokens}",
                job.Id, job.Kind, job.AttemptCount, result.ModelId, result.InputTokens, result.OutputTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down. Put it back so the next process picks it up rather than recording a
            // failure the provider never caused.
            job.Fail("AI_INTERRUPTED", "The job was interrupted by a shutdown.", retryable: true, clock.UtcNow);
            throw;
        }
        catch (AiProviderException ex)
        {
            job.Fail(ex.ErrorCode, ex.Message, ex.Retryable, clock.UtcNow);

            logger.LogWarning(
                "AI job failed job={JobId} kind={Kind} attempt={Attempt} code={ErrorCode} retryable={Retryable}",
                job.Id, job.Kind, job.AttemptCount, ex.ErrorCode, ex.Retryable);
        }
        catch (Exception ex)
        {
            // An unrecognised failure is treated as retryable but logged loudly: it is a bug here
            // rather than a fault at the provider, and the attempt limit still bounds it.
            job.Fail("AI_UNEXPECTED_ERROR", ex.Message, retryable: true, clock.UtcNow);
            logger.LogError(ex, "AI job {JobId} failed unexpectedly", job.Id);
        }
    }

    /// <summary>
    /// Assembles what the model is shown.
    ///
    /// Only the session's own timeline and metadata. No user identities, no credentials, no stream
    /// keys, no device tokens — none of which would help the analysis, and all of which would be
    /// sent to a third party if they were included by accident. The shape of this method is the
    /// control.
    /// </summary>
    private async Task<AiAnalysisRequest> BuildRequestAsync(AiJob job, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
                          .AsNoTracking()
                          .FirstOrDefaultAsync(candidate => candidate.Id == job.LiveSessionId, cancellationToken)
                      ?? throw new AiProviderException("AI_SESSION_GONE",
                          "The session this job belongs to no longer exists.", retryable: false);

        var sessionEvents = await db.LiveSessionEvents
            .AsNoTracking()
            .Where(entry => entry.LiveSessionId == job.LiveSessionId)
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => new { entry.CreatedAt, entry.Type, entry.ToStatus, entry.ErrorCode, entry.Detail })
            .ToListAsync(cancellationToken);

        var sourceEvents = await db.SourceEvents
            .AsNoTracking()
            .Where(entry => db.SessionSources
                .Any(source => source.Id == entry.SessionSourceId && source.LiveSessionId == job.LiveSessionId))
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => new { entry.CreatedAt, entry.Type, entry.ToStatus, entry.ErrorCode, entry.Detail })
            .ToListAsync(cancellationToken);

        var destinationEvents = await db.DestinationEvents
            .AsNoTracking()
            .Where(entry => db.StreamDestinations
                .Any(destination => destination.Id == entry.StreamDestinationId
                                    && destination.LiveSessionId == job.LiveSessionId))
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => new { entry.CreatedAt, entry.Type, entry.ToStatus, entry.ErrorCode, entry.Detail })
            .ToListAsync(cancellationToken);

        var startedAt = session.StartedAt;

        var timeline = sessionEvents
            .Select(entry => Entry("Session", entry.CreatedAt, entry.Type.ToString(), entry.ToStatus?.ToString(),
                entry.ErrorCode, entry.Detail, startedAt))
            .Concat(sourceEvents.Select(entry => Entry("Device", entry.CreatedAt, entry.Type.ToString(),
                entry.ToStatus?.ToString(), entry.ErrorCode, entry.Detail, startedAt)))
            .Concat(destinationEvents.Select(entry => Entry("Destination", entry.CreatedAt, entry.Type.ToString(),
                entry.ToStatus?.ToString(), entry.ErrorCode, entry.Detail, startedAt)))
            .OrderBy(entry => entry.At)
            .ToList();

        return new AiAnalysisRequest(
            job.Kind,
            session.Title,
            session.Description,
            session.StartedAt,
            session.EndedAt,
            Duration(session),
            Sample(timeline, _options.MaxTimelineEntries));
    }

    private static AiTimelineEntry Entry(string category, DateTimeOffset at, string type, string? toStatus,
        string? errorCode, string? detail, DateTimeOffset? startedAt)
    {
        var parts = new[] { toStatus, errorCode, detail }.Where(part => !string.IsNullOrWhiteSpace(part));

        return new AiTimelineEntry(
            at,
            startedAt is null ? null : Math.Round((at - startedAt.Value).TotalSeconds, 1),
            category,
            type,
            parts.Any() ? string.Join(" · ", parts) : null);
    }

    private static double? Duration(LiveSession session) =>
        session.StartedAt is { } started && session.EndedAt is { } ended
            ? Math.Round((ended - started).TotalSeconds, 1)
            : null;

    /// <summary>
    /// Reduces a long timeline to a fixed budget, keeping the ends.
    ///
    /// Evenly sampled rather than truncated: a four-hour broadcast's last hour is exactly the part
    /// a recap needs, and taking the first N entries would throw it away. The first and last entries
    /// are always kept so the range the job records is the range it was actually given.
    /// </summary>
    public static IReadOnlyList<AiTimelineEntry> Sample(IReadOnlyList<AiTimelineEntry> timeline, int budget)
    {
        if (budget <= 0) return [];
        if (timeline.Count <= budget) return timeline;
        if (budget == 1) return [timeline[0]];

        var kept = new List<AiTimelineEntry>(budget);
        var step = (timeline.Count - 1) / (double)(budget - 1);

        for (var i = 0; i < budget; i++)
        {
            var index = (int)Math.Round(i * step);
            var entry = timeline[Math.Min(index, timeline.Count - 1)];

            if (kept.Count == 0 || !ReferenceEquals(kept[^1], entry))
            {
                kept.Add(entry);
            }
        }

        return kept;
    }
}

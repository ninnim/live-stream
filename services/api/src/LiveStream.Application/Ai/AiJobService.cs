using LiveStream.Application.Abstractions;
using LiveStream.Application.Ai.Contracts;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Ai;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Ai;

/// <summary>
/// Queues and reads AI jobs (implementation/phase-6-ai-live-operations.md).
///
/// Nothing here calls a model. Requesting a job writes a row and returns; the work happens in a
/// background worker. That is the whole point of the phase's first acceptance criterion, and it is
/// also what keeps a slow or broken provider from ever appearing as a slow API.
/// </summary>
public sealed class AiJobService(
    IAppDbContext db,
    LiveSessionAuthorizationService authorization,
    IAiAnalyst analyst,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<AiOptions> options,
    ILogger<AiJobService> logger)
{
    private readonly AiOptions _options = options.Value;

    /// <summary>What this deployment can do. Read before offering a button that cannot work.</summary>
    public AiCapabilitiesResponse Describe() => new(
        analyst.IsConfigured && _options.Enabled,
        Enum.GetValues<AiJobKind>()
            .Select(kind => AiMapper.Describe(kind, _options.IsFeatureEnabled(kind) && analyst.IsConfigured))
            .ToList());

    public async Task<IReadOnlyList<AiJobResponse>> ListAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.AnalyticsView, cancellationToken);

        var jobs = await db.AiJobs
            .AsNoTracking()
            .Where(job => job.LiveSessionId == sessionId)
            .OrderByDescending(job => job.RequestedAt)
            .ToListAsync(cancellationToken);

        return jobs.Select(AiMapper.ToResponse).ToList();
    }

    /// <summary>
    /// Queues a job, or hands back the one already running.
    ///
    /// Deduplicated per session and kind because each attempt costs money, and an operator pressing
    /// a button twice means "I want this", not "I want to pay twice".
    /// </summary>
    public async Task<AiJobResponse> RequestAsync(Guid sessionId, Guid userId, RequestAiJobRequest request,
        CancellationToken cancellationToken)
    {
        var kind = ParseKind(request.Kind);

        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.AnalyticsView, cancellationToken);

        if (!analyst.IsConfigured)
        {
            throw new DomainException(ErrorCodes.AiUnavailable,
                "No AI provider is configured for this deployment.");
        }

        if (!_options.IsFeatureEnabled(kind))
        {
            throw new DomainException(ErrorCodes.AiFeatureDisabled,
                $"The {kind} feature is turned off for this deployment.");
        }

        var existing = await db.AiJobs
            .Where(job => job.LiveSessionId == sessionId
                          && job.Kind == kind
                          && (job.Status == AiJobStatus.Queued || job.Status == AiJobStatus.Running))
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return AiMapper.ToResponse(existing);
        }

        var job = AiJob.Queue(sessionId, session.WorkspaceId, kind, userId, clock.UtcNow);
        db.AiJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AI job queued session={SessionId} job={JobId} kind={Kind} correlationId={CorrelationId}",
            sessionId, job.Id, kind, correlation.CorrelationId);

        return AiMapper.ToResponse(job);
    }

    /// <summary>Withdraws a job that has not started yet.</summary>
    public async Task<AiJobResponse> CancelAsync(Guid sessionId, Guid jobId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.AnalyticsView, cancellationToken);

        var job = await db.AiJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
                  ?? throw new DomainException(ErrorCodes.AiJobNotFound, "AI job not found.");

        // Checking the parent rather than trusting the route, as everywhere else: a job id from one
        // session must not be operable through another the caller can reach.
        if (job.LiveSessionId != sessionId)
        {
            throw new DomainException(ErrorCodes.AiJobNotFound, "AI job not found.");
        }

        job.Cancel(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        return AiMapper.ToResponse(job);
    }

    private static AiJobKind ParseKind(string? value)
    {
        if (!Enum.TryParse<AiJobKind>(value, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown AI job kind '{value}'.");
        }

        return kind;
    }

    private async Task<LiveSession> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.LiveSessions.FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken)
        ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");
}

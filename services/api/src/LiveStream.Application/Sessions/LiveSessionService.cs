using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Application.Recordings;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Sessions;

/// <summary>
/// Owns the Live Session lifecycle. This is the authority for persistent session state:
/// clients request transitions, they never assert them (ai/architecture-rules.md).
/// </summary>
public sealed class LiveSessionService(
    IAppDbContext db,
    IMediaGateway mediaGateway,
    LiveSessionAuthorizationService authorization,
    RecordingService recordingService,
    TenantLimitService tenantLimits,
    ILiveSessionNotifier notifier,
    IDistributionCoordinator distribution,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<LiveSessionOptions> options,
    ILogger<LiveSessionService> logger)
{
    private readonly LiveSessionOptions _options = options.Value;

    // -----------------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------------

    public async Task<LiveSessionResponse> GetAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);
        return Project(session);
    }

    public async Task<LiveSessionStatusResponse> GetStatusAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);
        return LiveSessionMapper.ToStatusResponse(session, clock.UtcNow);
    }

    public async Task<LiveSessionHealthResponse> GetHealthAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);
        return LiveSessionMapper.ToHealthResponse(session.Health, _options.RecoveryWindow, clock.UtcNow);
    }

    public async Task<PagedResponse<LiveSessionResponse>> ListAsync(Guid userId, string? statusFilter, int page,
        int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Restricting to the caller's workspaces at the query level is what enforces tenant isolation
        // for list endpoints — there is no post-filtering that could be forgotten.
        var workspaceIds = db.WorkspaceMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId);

        var query = db.LiveSessions
            .AsNoTracking()
            .Include(s => s.Health)
            .Where(s => workspaceIds.Contains(s.WorkspaceId));

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            if (!Enum.TryParse<LiveSessionStatus>(statusFilter, ignoreCase: true, out var status))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown session status '{statusFilter}'.");
            }

            query = query.Where(s => s.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<LiveSessionResponse>(
            items.Select(Project).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<IReadOnlyList<LiveSessionEventResponse>> ListEventsAsync(Guid sessionId, Guid userId, int limit,
        CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);

        var events = await db.LiveSessionEvents
            .AsNoTracking()
            .Where(e => e.LiveSessionId == sessionId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return events.Select(LiveSessionMapper.ToResponse).ToList();
    }

    /// <summary>
    /// Playback information for a viewer. Private sessions require workspace membership; public and
    /// unlisted sessions are readable by anyone with the link.
    /// </summary>
    public async Task<PlaybackResponse> GetPlaybackAsync(Guid sessionId, Guid? userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);

        if (!await authorization.CanViewPlaybackAsync(session, userId, cancellationToken))
        {
            throw new DomainException(ErrorCodes.PermissionDenied, "This live session is private.");
        }

        return BuildPlayback(session);
    }

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    public async Task<LiveSessionResponse> CreateAsync(Guid userId, Guid? workspaceId,
        CreateLiveSessionRequest request, CancellationToken cancellationToken)
    {
        var targetWorkspaceId = workspaceId ?? await ResolveDefaultWorkspaceAsync(userId, cancellationToken);

        var role = await authorization.GetRoleAsync(targetWorkspaceId, userId, cancellationToken);
        if (role is null || !WorkspacePermissions.Allows(role.Value, WorkspacePermission.LiveSessionEdit))
        {
            throw new DomainException(ErrorCodes.PermissionDenied,
                "You do not have permission to create sessions in this workspace.");
        }

        await EnsureConcurrentSessionLimitAsync(targetWorkspaceId, cancellationToken);

        var visibility = ParseVisibility(request.Visibility);
        var now = clock.UtcNow;
        var session = LiveSession.Create(targetWorkspaceId, userId, request.Title, request.Description,
            visibility, request.RecordingEnabled, now);

        db.LiveSessions.Add(session);
        await SaveAsync(cancellationToken);

        logger.LogInformation(
            "Live session created {SessionId} workspace={WorkspaceId} visibility={Visibility} recording={RecordingEnabled} correlationId={CorrelationId}",
            session.Id, targetWorkspaceId, visibility, request.RecordingEnabled, correlation.CorrelationId);

        return Project(session);
    }

    public async Task<LiveSessionResponse> UpdateAsync(Guid sessionId, Guid userId,
        UpdateLiveSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionEdit, cancellationToken);

        session.UpdateDetails(request.Title, request.Description, ParseVisibility(request.Visibility),
            request.RecordingEnabled, clock.UtcNow);

        await SaveAsync(cancellationToken);
        return Project(session);
    }

    /// <summary>
    /// DRAFT → PREPARING → READY. Provisions the ingest path on the media plane. Re-running prepare
    /// on a READY session is allowed so a studio reload can safely re-verify readiness.
    /// </summary>
    public async Task<LiveSessionResponse> PrepareAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        // Residency is checked where media is about to be provisioned: this is the moment the
        // tenant's bytes would land in this region.
        tenantLimits.EnsureRegionAllowed(await tenantLimits.ResolveAsync(session.WorkspaceId, cancellationToken));

        var now = clock.UtcNow;

        if (session.Status is LiveSessionStatus.Ready)
        {
            return Project(session); // Idempotent.
        }

        if (session.Status is not LiveSessionStatus.Draft)
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                $"A session in state {session.Status.ToString().ToUpperInvariant()} cannot be prepared.");
        }

        session.TransitionTo(LiveSessionStatus.Preparing, now, userId, "Allocating media resources",
            correlationId: correlation.CorrelationId);
        await SaveAsync(cancellationToken);
        await PublishStateAsync(session, cancellationToken);

        MediaPathProvisionResult provision;
        try
        {
            provision = await mediaGateway.ProvisionPathAsync(
                new MediaPathRequest(session.Id, session.MediaPathName, session.RecordingEnabled),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Media path provisioning threw for session {SessionId} correlationId={CorrelationId}",
                session.Id, correlation.CorrelationId);
            provision = new MediaPathProvisionResult(false, mediaGateway.DescribeEndpoints(session.MediaPathName),
                "The media service is unavailable.");
        }

        now = clock.UtcNow;
        if (!provision.Success)
        {
            session.TransitionTo(LiveSessionStatus.Failed, now, userId, provision.FailureReason,
                ErrorCodes.MediaGatewayUnavailable, correlation.CorrelationId);
            await SaveAsync(cancellationToken);
            await PublishStateAsync(session, cancellationToken);

            logger.LogError(
                "Live session prepare failed {SessionId} reason={Reason} correlationId={CorrelationId}",
                session.Id, provision.FailureReason, correlation.CorrelationId);

            throw new DomainException(ErrorCodes.MediaGatewayUnavailable,
                "We could not reserve streaming resources. Please try again in a moment.");
        }

        if (session.RecordingEnabled)
        {
            await recordingService.EnsurePendingRecordingAsync(session, cancellationToken);
        }

        session.TransitionTo(LiveSessionStatus.Ready, now, userId,
            $"Ingest path ready via {mediaGateway.ProviderName}", correlationId: correlation.CorrelationId);
        await SaveAsync(cancellationToken);
        await PublishStateAsync(session, cancellationToken);

        logger.LogInformation(
            "Live session prepared {SessionId} provider={Provider} recording={RecordingEnabled} correlationId={CorrelationId}",
            session.Id, mediaGateway.ProviderName, session.RecordingEnabled, correlation.CorrelationId);

        return Project(session);
    }

    /// <summary>
    /// READY → STARTING, promoted to LIVE as soon as the media plane confirms a publisher.
    /// The order of "connect ingest" and "call start" does not matter: if media is already flowing
    /// the session goes LIVE immediately, otherwise the health monitor promotes it when ingest
    /// appears, or fails it after <see cref="LiveSessionOptions.StartIngestTimeout"/>.
    /// </summary>
    public async Task<LiveSessionStatusResponse> StartAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        var now = clock.UtcNow;

        if (LiveSessionStateMachine.IsBroadcasting(session.Status))
        {
            return LiveSessionMapper.ToStatusResponse(session, now); // Idempotent restart of an active session.
        }

        if (session.Status is not (LiveSessionStatus.Ready or LiveSessionStatus.Starting))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "The session must be prepared before it can go live.");
        }

        session.TransitionTo(LiveSessionStatus.Starting, now, userId, "Start requested",
            correlationId: correlation.CorrelationId);
        session.Health.RecoveryStartedAt = null;
        await SaveAsync(cancellationToken);
        await PublishStateAsync(session, cancellationToken);

        var state = await SafeGetPathStateAsync(session.MediaPathName, cancellationToken);
        if (state is { PublisherConnected: true })
        {
            now = clock.UtcNow;
            session.TransitionTo(LiveSessionStatus.Live, now, userId, "Ingest confirmed",
                correlationId: correlation.CorrelationId);
            session.Health.IngestConnected = true;
            session.Health.LastIngestAt = now;
            session.Health.ObservedAt = now;

            if (session.RecordingEnabled)
            {
                await recordingService.MarkRecordingStartedAsync(session, cancellationToken);
            }

            await SaveAsync(cancellationToken);
            await PublishStateAsync(session, cancellationToken);

            // External platforms are fanned out from here. The coordinator never throws, so nothing
            // it does can stop this session being LIVE.
            await distribution.OnSessionLiveAsync(session.Id, cancellationToken);

            logger.LogInformation(
                "Live session started {SessionId} state=LIVE correlationId={CorrelationId}",
                session.Id, correlation.CorrelationId);
        }
        else
        {
            logger.LogInformation(
                "Live session start pending ingest {SessionId} timeoutSeconds={Timeout} correlationId={CorrelationId}",
                session.Id, _options.StartIngestTimeoutSeconds, correlation.CorrelationId);
        }

        return LiveSessionMapper.ToStatusResponse(session, clock.UtcNow);
    }

    /// <summary>
    /// Any active state → STOPPING → ENDED. Releases the media path and finalizes recording metadata.
    /// A media-plane failure during teardown must not prevent the session from reaching ENDED.
    /// </summary>
    public async Task<LiveSessionStatusResponse> StopAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStop, cancellationToken);

        var now = clock.UtcNow;

        if (session.Status is LiveSessionStatus.Ended)
        {
            return LiveSessionMapper.ToStatusResponse(session, now); // Idempotent.
        }

        if (!LiveSessionStateMachine.CanTransition(session.Status, LiveSessionStatus.Stopping)
            && session.Status is not LiveSessionStatus.Stopping)
        {
            throw new DomainException(ErrorCodes.InvalidStateTransition,
                $"A session in state {session.Status.ToString().ToUpperInvariant()} cannot be stopped.");
        }

        session.TransitionTo(LiveSessionStatus.Stopping, now, userId, "Stop requested",
            correlationId: correlation.CorrelationId);
        await SaveAsync(cancellationToken);
        await PublishStateAsync(session, cancellationToken);

        // Destinations are drained before the media path goes away, so each relay stops against a
        // source that still exists rather than reporting a spurious read failure on the way out.
        await distribution.OnSessionEndedAsync(session.Id, cancellationToken);

        await RevokeCredentialsAsync(session.Id, now, cancellationToken);

        try
        {
            await mediaGateway.ReleasePathAsync(session.MediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session must still end cleanly; an orphaned media path is reconciled by the monitor.
            logger.LogWarning(ex,
                "Releasing media path failed during stop {SessionId} correlationId={CorrelationId}",
                session.Id, correlation.CorrelationId);
        }

        if (session.RecordingEnabled)
        {
            await recordingService.FinalizeAsync(session, cancellationToken);
        }

        now = clock.UtcNow;
        session.TransitionTo(LiveSessionStatus.Ended, now, userId, "Stopped by user",
            correlationId: correlation.CorrelationId);
        session.Health.IngestConnected = false;
        session.Health.RecoveryStartedAt = null;
        session.Health.ViewerCount = 0;
        session.Health.ObservedAt = now;
        await SaveAsync(cancellationToken);
        await PublishStateAsync(session, cancellationToken);

        var duration = session.StartedAt is null ? 0 : (int)(now - session.StartedAt.Value).TotalSeconds;
        logger.LogInformation(
            "Live session stopped {SessionId} durationSeconds={Duration} reconnects={Reconnects} correlationId={CorrelationId}",
            session.Id, duration, session.Health.ReconnectCount, correlation.CorrelationId);

        return LiveSessionMapper.ToStatusResponse(session, now);
    }

    /// <summary>
    /// Records a broadcaster-reported transport signal. Advisory only — it is written to the session
    /// event log for diagnostics and never changes session state, because clients are not trusted
    /// to declare state (ai/architecture-rules.md).
    /// </summary>
    public async Task RecordBroadcasterSignalAsync(Guid sessionId, Guid userId, BroadcasterSignalRequest request,
        CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        var eventType = request.Signal.ToUpperInvariant() switch
        {
            "INGEST_CONNECTED" => LiveSessionEventType.IngestConnected,
            "INGEST_DISCONNECTED" => LiveSessionEventType.IngestDisconnected,
            "RECONNECT_ATTEMPT" => LiveSessionEventType.ReconnectAttempted,
            "RECONNECT_SUCCEEDED" => LiveSessionEventType.ReconnectSucceeded,
            "RECONNECT_FAILED" => LiveSessionEventType.ReconnectFailed,
            "DEVICE_ERROR" or "CLIENT_ERROR" => LiveSessionEventType.ClientError,
            _ => throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown broadcaster signal '{request.Signal}'."),
        };

        // Truncated so a noisy client cannot bloat the event log.
        var detail = request.Detail is { Length: > 500 } ? request.Detail[..500] : request.Detail;

        session.RecordEvent(eventType, clock.UtcNow, userId, detail, correlationId: correlation.CorrelationId);
        await SaveAsync(cancellationToken);

        logger.LogInformation(
            "Broadcaster signal {Signal} session={SessionId} correlationId={CorrelationId}",
            eventType, session.Id, correlation.CorrelationId);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>Loads a tracked session with its owned health row, or throws a stable not-found error.</summary>
    public async Task<LiveSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
            .Include(s => s.Health)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        return session ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");
    }

    public LiveSessionResponse Project(LiveSession session) =>
        LiveSessionMapper.ToResponse(session, BuildPlayback(session)) with
        {
            Health = LiveSessionMapper.ToHealthResponse(session.Health, _options.RecoveryWindow, clock.UtcNow),
        };

    public PlaybackResponse BuildPlayback(LiveSession session)
    {
        var endpoints = mediaGateway.DescribeEndpoints(session.MediaPathName);
        return new PlaybackResponse(
            endpoints.HlsPlaybackUrl,
            endpoints.WebRtcPlaybackUrl,
            LiveSessionStateMachine.IsBroadcasting(session.Status));
    }

    public async Task PublishStateAsync(LiveSession session, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.SessionStateChangedAsync(
                LiveSessionMapper.ToStatusResponse(session, clock.UtcNow), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Realtime delivery is best-effort; the REST status endpoint remains authoritative.
            logger.LogWarning(ex, "Publishing session state failed {SessionId}", session.Id);
        }
    }

    private async Task<MediaPathState?> SafeGetPathStateAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        try
        {
            return await mediaGateway.GetPathStateAsync(mediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Media path state lookup failed for {MediaPath}", mediaPathName);
            return null;
        }
    }

    private async Task RevokeCredentialsAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var credentials = await db.IngestCredentials
            .Where(c => c.LiveSessionId == sessionId && c.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            credential.Revoke(now);
        }
    }

    private async Task<Guid> ResolveDefaultWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
    {
        var workspaceId = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);

        return workspaceId ?? throw new DomainException(ErrorCodes.PermissionDenied,
            "You do not belong to a workspace.");
    }

    private async Task EnsureConcurrentSessionLimitAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var limits = await tenantLimits.ResolveAsync(workspaceId, cancellationToken);
        var limit = limits.MaxConcurrentSessions;

        var active = await db.LiveSessions
            .AsNoTracking()
            .CountAsync(s => s.WorkspaceId == workspaceId
                             && s.Status != LiveSessionStatus.Ended
                             && s.Status != LiveSessionStatus.Failed,
                cancellationToken);

        if (active >= limit)
        {
            throw new DomainException(ErrorCodes.PlanLimitReached,
                $"The {limits.Plan} plan allows {limit} open live sessions. End one before creating another.");
        }
    }

    private static LiveSessionVisibility ParseVisibility(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LiveSessionVisibility.Private;
        }

        if (!Enum.TryParse<LiveSessionVisibility>(value, ignoreCase: true, out var visibility))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Visibility must be one of PRIVATE, UNLISTED or PUBLIC.");
        }

        return visibility;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Concurrent live session update rejected correlationId={CorrelationId}",
                correlation.CorrelationId);
            throw new DomainException(ErrorCodes.ConcurrencyConflict,
                "This session was changed by another device. Refresh and try again.");
        }
    }
}

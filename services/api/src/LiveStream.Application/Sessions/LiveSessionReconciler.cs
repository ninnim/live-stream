using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Recordings;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Sessions;

/// <summary>
/// Reconciles authoritative session state against what the media plane actually reports.
///
/// This is where streaming reliability lives: a dropped ingest moves a session to RECONNECTING and
/// keeps it alive for a bounded recovery window rather than ending it, and quality degradation is
/// surfaced as DEGRADED instead of a disconnect (MASTER_BLUEPRINT.md §11, docs/03-streaming-engine.md).
///
/// The reconciler is the source of truth over media-gateway webhooks: hooks can be lost or
/// duplicated, so they only trigger an out-of-band reconcile of the same idempotent logic.
/// </summary>
public sealed class LiveSessionReconciler(
    IAppDbContext db,
    IMediaGateway mediaGateway,
    RecordingService recordingService,
    ILiveSessionNotifier notifier,
    IDistributionCoordinator distribution,
    IClock clock,
    IOptions<LiveSessionOptions> options,
    ILogger<LiveSessionReconciler> logger)
{
    private readonly LiveSessionOptions _options = options.Value;

    /// <summary>Statuses the reconciler is responsible for driving.</summary>
    private static readonly LiveSessionStatus[] ActiveStatuses =
    [
        LiveSessionStatus.Starting,
        LiveSessionStatus.Live,
        LiveSessionStatus.Degraded,
        LiveSessionStatus.Reconnecting,
    ];

    /// <summary>Reconciles every session currently expected to be carrying media.</summary>
    public async Task<int> ReconcileActiveSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = await db.LiveSessions
            .Include(s => s.Health)
            .Where(s => ActiveStatuses.Contains(s.Status))
            .ToListAsync(cancellationToken);

        var reconciled = 0;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ReconcileAsync(session, cancellationToken);
                reconciled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad session must never stop the monitor from servicing the others.
                logger.LogError(ex, "Reconciling live session {SessionId} failed", session.Id);
            }
        }

        return reconciled;
    }

    public async Task ReconcileAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
            .Include(s => s.Health)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is not null)
        {
            await ReconcileAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Applies one media-plane observation to a session. Idempotent: running it repeatedly with the
    /// same observation produces the same state and no duplicate events.
    /// </summary>
    public async Task ReconcileAsync(LiveSession session, CancellationToken cancellationToken)
    {
        if (!LiveSessionStateMachine.ExpectsIngest(session.Status))
        {
            return;
        }

        MediaPathState? state;
        try
        {
            state = await mediaGateway.GetPathStateAsync(session.MediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A gateway outage is not evidence that the broadcaster dropped: leave state untouched
            // rather than tearing down a healthy stream because the control plane cannot see it.
            logger.LogWarning(ex, "Media gateway unreachable while reconciling {SessionId}", session.Id);
            return;
        }

        var now = clock.UtcNow;
        var health = session.Health;
        var previousStatus = session.Status;
        var previousHealth = Snapshot(health);

        var publisherConnected = state?.PublisherConnected ?? false;
        var bitrateKbps = CalculateBitrateKbps(health, state, now);
        var viewerCount = state?.ReaderCount ?? 0;

        health.IngestConnected = publisherConnected;
        health.ViewerCount = viewerCount;
        health.BytesReceived = state?.BytesReceived ?? health.BytesReceived;
        health.BitrateKbps = publisherConnected ? bitrateKbps : null;
        health.ObservedAt = now;
        if (publisherConnected)
        {
            health.LastIngestAt = now;
        }

        switch (session.Status)
        {
            case LiveSessionStatus.Starting:
                await ReconcileStartingAsync(session, publisherConnected, now, cancellationToken);
                break;

            case LiveSessionStatus.Live or LiveSessionStatus.Degraded:
                ReconcileBroadcasting(session, publisherConnected, bitrateKbps, now);
                break;

            case LiveSessionStatus.Reconnecting:
                await ReconcileReconnectingAsync(session, publisherConnected, bitrateKbps, now, cancellationToken);
                break;
        }

        if (session.Status is LiveSessionStatus.Failed)
        {
            await TearDownAsync(session, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (session.Status != previousStatus)
        {
            await SafeNotifyAsync(() => notifier.SessionStateChangedAsync(
                LiveSessionMapper.ToStatusResponse(session, now), cancellationToken), session.Id);
        }

        if (HealthChanged(previousHealth, health))
        {
            await SafeNotifyAsync(() => notifier.HealthUpdatedAsync(session.Id,
                LiveSessionMapper.ToHealthResponse(health, _options.RecoveryWindow, now), cancellationToken), session.Id);
        }

        if (previousHealth.ViewerCount != health.ViewerCount)
        {
            await SafeNotifyAsync(() => notifier.ViewerCountUpdatedAsync(session.Id, health.ViewerCount,
                cancellationToken), session.Id);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Per-state rules
    // -----------------------------------------------------------------------------------------

    private async Task ReconcileStartingAsync(LiveSession session, bool publisherConnected, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (publisherConnected)
        {
            session.TransitionTo(LiveSessionStatus.Live, now, reason: "Ingest detected");
            session.Health.Status = StreamHealthStatus.Good;

            if (session.RecordingEnabled)
            {
                await recordingService.MarkRecordingStartedAsync(session, cancellationToken);
            }

            logger.LogInformation("Live session promoted to LIVE {SessionId}", session.Id);

            // The other place a session becomes LIVE. Both must fan out, or a broadcast that
            // connects its media after pressing Start would never reach its destinations.
            await distribution.OnSessionLiveAsync(session.Id, cancellationToken);
            return;
        }

        // The broadcaster asked to go live but no media arrived within the allowed window.
        if (now - session.StateEnteredAt > _options.StartIngestTimeout)
        {
            session.TransitionTo(LiveSessionStatus.Failed, now,
                reason: "No media reached the server after the start request.",
                errorCode: ErrorCodes.StreamStartFailed);
            session.Health.LastErrorCode = ErrorCodes.StreamStartFailed;

            logger.LogWarning(
                "Live session start timed out {SessionId} waitedSeconds={Waited}",
                session.Id, (int)(now - session.StateEnteredAt).TotalSeconds);
        }
    }

    private void ReconcileBroadcasting(LiveSession session, bool publisherConnected, int? bitrateKbps,
        DateTimeOffset now)
    {
        var health = session.Health;

        if (!publisherConnected)
        {
            // Rule 3: a temporary interruption becomes RECONNECTING, never ENDED.
            health.RecoveryStartedAt = now;
            health.ReconnectCount++;
            health.Status = StreamHealthStatus.Poor;
            health.LastErrorCode = ErrorCodes.SourceDisconnected;
            session.TransitionTo(LiveSessionStatus.Reconnecting, now,
                reason: "Ingest lost; waiting for the broadcaster to reconnect.",
                errorCode: ErrorCodes.SourceDisconnected);

            logger.LogWarning(
                "Live session ingest lost {SessionId} recoveryWindowSeconds={Window} reconnectCount={Count}",
                session.Id, _options.RecoveryWindowSeconds, health.ReconnectCount);
            return;
        }

        health.RecoveryStartedAt = null;
        health.Status = ClassifyHealth(bitrateKbps);

        if (health.Status is StreamHealthStatus.Poor && session.Status is LiveSessionStatus.Live)
        {
            health.LastErrorCode = ErrorCodes.NetworkDegraded;
            session.TransitionTo(LiveSessionStatus.Degraded, now,
                reason: $"Sustained low bitrate ({bitrateKbps} kbps).", errorCode: ErrorCodes.NetworkDegraded);

            logger.LogInformation("Live session degraded {SessionId} bitrateKbps={Bitrate}", session.Id, bitrateKbps);
        }
        else if (health.Status is not StreamHealthStatus.Poor && session.Status is LiveSessionStatus.Degraded)
        {
            health.LastErrorCode = null;
            session.TransitionTo(LiveSessionStatus.Live, now, reason: "Stream quality recovered.");

            logger.LogInformation("Live session recovered {SessionId} bitrateKbps={Bitrate}", session.Id, bitrateKbps);
        }
    }

    private async Task ReconcileReconnectingAsync(LiveSession session, bool publisherConnected, int? bitrateKbps,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var health = session.Health;

        if (publisherConnected)
        {
            health.RecoveryStartedAt = null;
            health.Status = ClassifyHealth(bitrateKbps);
            health.LastErrorCode = null;

            session.TransitionTo(LiveSessionStatus.Live, now, reason: "Broadcaster reconnected.");
            session.RecordEvent(LiveSessionEventType.ReconnectSucceeded, now,
                detail: $"reconnectCount={health.ReconnectCount}");

            if (session.RecordingEnabled)
            {
                await recordingService.MarkRecordingStartedAsync(session, cancellationToken);
            }

            logger.LogInformation("Live session reconnected {SessionId} reconnectCount={Count}",
                session.Id, health.ReconnectCount);
            return;
        }

        var recoveryStartedAt = health.RecoveryStartedAt ?? session.StateEnteredAt;
        if (now - recoveryStartedAt <= _options.RecoveryWindow)
        {
            return; // Still inside the bounded recovery window: hold the session open.
        }

        session.TransitionTo(LiveSessionStatus.Failed, now,
            reason: "The broadcaster did not reconnect within the recovery window.",
            errorCode: ErrorCodes.SourceDisconnected);
        session.RecordEvent(LiveSessionEventType.ReconnectFailed, now,
            detail: $"recoveryWindowSeconds={_options.RecoveryWindowSeconds}", errorCode: ErrorCodes.SourceDisconnected);
        health.LastErrorCode = ErrorCodes.SourceDisconnected;

        logger.LogWarning("Live session recovery window expired {SessionId} windowSeconds={Window}",
            session.Id, _options.RecoveryWindowSeconds);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>Releases media resources and closes out the recording after an unrecoverable failure.</summary>
    private async Task TearDownAsync(LiveSession session, CancellationToken cancellationToken)
    {
        // A failed session still has relays running against a path that is about to disappear.
        await distribution.OnSessionEndedAsync(session.Id, cancellationToken);

        try
        {
            await mediaGateway.ReleasePathAsync(session.MediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Releasing media path after failure did not succeed {SessionId}", session.Id);
        }

        if (session.RecordingEnabled)
        {
            await recordingService.FinalizeAsync(session, cancellationToken);
        }
    }

    private StreamHealthStatus ClassifyHealth(int? bitrateKbps) => bitrateKbps switch
    {
        null => StreamHealthStatus.Unknown,
        var kbps when kbps >= _options.HealthyBitrateKbps => StreamHealthStatus.Good,
        var kbps when kbps >= _options.PoorBitrateKbps => StreamHealthStatus.Fair,
        _ => StreamHealthStatus.Poor,
    };

    /// <summary>
    /// Derives bitrate from the byte counter delta between observations. Returns null on the first
    /// observation or when the counter resets (a new publisher), so a reset is never read as 0 kbps.
    /// </summary>
    private static int? CalculateBitrateKbps(LiveSessionHealth health, MediaPathState? state, DateTimeOffset now)
    {
        if (state is null || health.ObservedAt is not { } previousObservedAt)
        {
            return null;
        }

        var elapsed = (now - previousObservedAt).TotalSeconds;
        if (elapsed <= 0.5)
        {
            return health.BitrateKbps;
        }

        var deltaBytes = state.BytesReceived - health.BytesReceived;
        if (deltaBytes < 0)
        {
            return null;
        }

        return (int)Math.Round(deltaBytes * 8 / elapsed / 1000);
    }

    private static HealthSnapshot Snapshot(LiveSessionHealth health) => new(
        health.Status, health.IngestConnected, health.BitrateKbps, health.ViewerCount, health.ReconnectCount);

    private static bool HealthChanged(HealthSnapshot before, LiveSessionHealth after) =>
        before.Status != after.Status
        || before.IngestConnected != after.IngestConnected
        || before.ReconnectCount != after.ReconnectCount
        // Ignore sub-100 kbps jitter so the realtime channel is not flooded every poll.
        || Math.Abs((before.BitrateKbps ?? 0) - (after.BitrateKbps ?? 0)) >= 100;

    private async Task SafeNotifyAsync(Func<Task> publish, Guid sessionId)
    {
        try
        {
            await publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Publishing realtime update failed {SessionId}", sessionId);
        }
    }

    private readonly record struct HealthSnapshot(
        StreamHealthStatus Status,
        bool IngestConnected,
        int? BitrateKbps,
        int ViewerCount,
        int ReconnectCount);
}

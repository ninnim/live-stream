using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Media;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Distribution;

/// <summary>
/// Drives destinations through their lifecycle and keeps them reconciled against the relay.
///
/// The contract this class exists to honour, from implementation/phase-2-multi-platform-distribution.md:
/// <i>"Core stream can remain LIVE if one destination fails."</i>
///
/// Consequently nothing here is allowed to propagate a failure into session logic. Every public
/// method that the session lifecycle calls swallows and records errors rather than throwing, and
/// per-destination work is isolated so one bad platform cannot stop the others from starting.
/// </summary>
public sealed class DestinationOrchestrator(
    IAppDbContext db,
    IStreamRelay relay,
    IDestinationProviderRegistry providers,
    ProviderAccountService accounts,
    IngestCredentialService credentials,
    LiveSessionAuthorizationService authorization,
    ISecretProtector secretProtector,
    ILiveSessionNotifier notifier,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<DistributionOptions> options,
    ILogger<DestinationOrchestrator> logger) : IDistributionCoordinator
{
    private readonly DistributionOptions _options = options.Value;

    // -----------------------------------------------------------------------------------------
    // Session lifecycle hooks — called through IDistributionCoordinator. Never throw.
    // -----------------------------------------------------------------------------------------

    public Task OnSessionLiveAsync(Guid sessionId, CancellationToken cancellationToken) =>
        StartForSessionAsync(sessionId, cancellationToken);

    public Task OnSessionEndedAsync(Guid sessionId, CancellationToken cancellationToken) =>
        StopForSessionAsync(sessionId, cancellationToken);

    /// <summary>
    /// Starts every enabled destination for a session that has just gone LIVE.
    ///
    /// Returns the number started. Failures are recorded on the destinations themselves; the caller
    /// is not told about them because it must not act on them.
    /// </summary>
    public async Task<int> StartForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null)
            {
                return 0;
            }

            var destinations = await db.StreamDestinations
                .Include(d => d.ProviderAccount)
                .Where(d => d.LiveSessionId == sessionId && d.Enabled)
                .ToListAsync(cancellationToken);

            var startable = destinations.Where(d => d.IsStartable).ToList();
            if (startable.Count == 0)
            {
                return 0;
            }

            // One credential serves every destination for this session: they all read the same path,
            // and issuing one per destination would multiply revocation work for no security gain.
            var sourceCredential = await credentials.IssueRelayReadCredentialAsync(
                session, _options.RelaySourceCredentialLifetime, cancellationToken);

            var started = 0;
            foreach (var destination in startable)
            {
                destination.ResetForRun(clock.UtcNow);

                if (await TryBeginAttemptAsync(destination, session, sourceCredential, cancellationToken))
                {
                    started++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await PublishAllAsync(startable, cancellationToken);

            logger.LogInformation(
                "Destinations started for session {SessionId}: {Started}/{Total} correlationId={CorrelationId}",
                sessionId, started, startable.Count, correlation.CorrelationId);

            return started;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A total failure to start distribution is a distribution problem, not a session problem.
            logger.LogError(ex, "Starting destinations for session {SessionId} failed", sessionId);
            return 0;
        }
    }

    /// <summary>Stops every active destination for a session. Never throws.</summary>
    public async Task StopForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var destinations = await db.StreamDestinations
                .Include(d => d.ProviderAccount)
                .Where(d => d.LiveSessionId == sessionId)
                .ToListAsync(cancellationToken);

            var active = destinations.Where(d => DestinationStateMachine.IsActive(d.Status)).ToList();
            if (active.Count == 0)
            {
                return;
            }

            foreach (var destination in active)
            {
                await StopOneAsync(destination, "Session ended.", cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await PublishAllAsync(active, cancellationToken);

            logger.LogInformation("Stopped {Count} destinations for session {SessionId}", active.Count, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Stopping destinations for session {SessionId} failed", sessionId);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Reconciliation
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reconciles every destination that should currently be running against what the relay reports.
    /// Called on a timer, in the same spirit as <see cref="LiveSessionReconciler"/>: relay reports
    /// can be lost, so polled state is authoritative rather than event-driven state.
    /// </summary>
    public async Task<int> ReconcileActiveDestinationsAsync(CancellationToken cancellationToken)
    {
        var activeStatuses = DestinationStateMachine.ActiveStates.ToArray();

        var destinations = await db.StreamDestinations
            .Include(d => d.ProviderAccount)
            .Include(d => d.LiveSession)
            .Where(d => activeStatuses.Contains(d.Status))
            .ToListAsync(cancellationToken);

        if (destinations.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<RelayState> relayStates;
        try
        {
            relayStates = await relay.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as a media gateway outage: not being able to see the relay is not
            // evidence that destinations dropped. Leave state untouched and try again next tick.
            logger.LogWarning(ex, "Relay unreachable while reconciling destinations");
            return 0;
        }

        var byId = relayStates.ToDictionary(s => s.DestinationId);
        var changed = new List<StreamDestination>();

        foreach (var destination in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var before = destination.Status;
                await ReconcileOneAsync(destination, byId.GetValueOrDefault(destination.Id), cancellationToken);

                if (destination.Status != before)
                {
                    changed.Add(destination);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad destination must never stop the others from being serviced.
                logger.LogError(ex, "Reconciling destination {DestinationId} failed", destination.Id);
            }
        }

        if (changed.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await PublishAllAsync(changed, cancellationToken);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return destinations.Count;
    }

    private async Task ReconcileOneAsync(StreamDestination destination, RelayState? relayState,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // A destination whose session is no longer broadcasting has been orphaned — most often by an
        // API restart between the session stopping and the relay being torn down.
        if (destination.LiveSession is { } session && !LiveSessionStateMachine.IsBroadcasting(session.Status))
        {
            await StopOneAsync(destination, "Session is no longer broadcasting.", cancellationToken);
            return;
        }

        if (destination.Status == DestinationStatus.Retrying)
        {
            if (destination.IsRetryDue(now))
            {
                await RetryAsync(destination, cancellationToken);
            }

            return;
        }

        if (relayState is null)
        {
            // The relay has no record of a destination we believe is running: it restarted, or the
            // process was never accepted. Treat it as a disconnect and go through the retry path.
            await HandleFailureAsync(destination, ErrorCodes.RelayUnavailable,
                "The relay is no longer running this destination.", retryable: true, cancellationToken);
            return;
        }

        destination.RecordBytesSent(relayState.BytesSent, now);

        switch (relayState.Phase)
        {
            case RelayPhase.Connected:
                if (destination.Status != DestinationStatus.Live)
                {
                    destination.TransitionTo(DestinationStatus.Live, now,
                        reason: "The platform is accepting media.", correlationId: correlation.CorrelationId);

                    await NotifyProviderLiveAsync(destination, cancellationToken);
                }

                break;

            case RelayPhase.Failed:
                await HandleFailureAsync(destination,
                    relayState.LastErrorCode ?? ErrorCodes.DestinationUnavailable,
                    relayState.LastErrorMessage ?? "The connection to the platform failed.",
                    IsRetryable(relayState.LastErrorCode), cancellationToken);
                break;

            case RelayPhase.Stopped:
                await StopOneAsync(destination, "The relay stopped.", cancellationToken);
                break;

            case RelayPhase.Starting:
                // A platform accepts an RTMP handshake quickly or not at all, so a long CONNECTING
                // is a failure that has not reported itself yet.
                if (destination.Status is DestinationStatus.Connecting or DestinationStatus.Preparing
                    && now - destination.StateEnteredAt > _options.ConnectTimeout)
                {
                    await HandleFailureAsync(destination, ErrorCodes.DestinationUnavailable,
                        $"The platform did not accept the connection within {_options.ConnectTimeoutSeconds} seconds.",
                        retryable: true, cancellationToken);
                }

                break;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Operator actions
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Starts or restarts one destination on request. Unlike the lifecycle hooks this does throw,
    /// because an operator pressing a button is entitled to know it did not work.
    /// </summary>
    public async Task<DestinationResponse> StartDestinationAsync(Guid sessionId, Guid destinationId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (session, destination) = await LoadForOperatorAsync(sessionId, destinationId, userId, cancellationToken);

        if (!LiveSessionStateMachine.IsBroadcasting(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "Start the broadcast before starting a destination.");
        }

        if (!destination.Enabled)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Enable this destination before starting it.");
        }

        if (DestinationStateMachine.IsActive(destination.Status))
        {
            return DestinationMapper.ToResponse(destination, clock.UtcNow);
        }

        var sourceCredential = await credentials.IssueRelayReadCredentialAsync(
            session, _options.RelaySourceCredentialLifetime, cancellationToken);

        destination.ResetForRun(clock.UtcNow);
        await TryBeginAttemptAsync(destination, session, sourceCredential, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(destination, cancellationToken);

        return DestinationMapper.ToResponse(destination, clock.UtcNow);
    }

    /// <summary>Stops one destination on request, leaving the session running.</summary>
    public async Task<DestinationResponse> StopDestinationAsync(Guid sessionId, Guid destinationId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (_, destination) = await LoadForOperatorAsync(sessionId, destinationId, userId, cancellationToken);

        await StopOneAsync(destination, "Stopped by operator.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(destination, cancellationToken);

        return DestinationMapper.ToResponse(destination, clock.UtcNow);
    }

    // -----------------------------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the target and hands it to the relay. Returns false when the attempt did not get as
    /// far as a running relay; the destination has already been moved to RETRYING or ERROR.
    /// </summary>
    private async Task<bool> TryBeginAttemptAsync(StreamDestination destination, LiveSession session,
        string sourceCredential, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        try
        {
            destination.TransitionTo(DestinationStatus.Preparing, now,
                reason: "Resolving the publishing target.", correlationId: correlation.CorrelationId);

            var adapter = providers.Get(destination.Provider);
            var context = await BuildResolveContextAsync(destination, session, cancellationToken);

            if (context is null)
            {
                await HandleFailureAsync(destination, ErrorCodes.ProviderAccountUnavailable,
                    "The linked account needs to be connected again.", retryable: false, cancellationToken);
                return false;
            }

            var resolution = await adapter.ResolveTargetAsync(context, cancellationToken);

            if (!resolution.Success || resolution.IngestUrl is null || resolution.StreamKey is null)
            {
                await HandleFailureAsync(destination,
                    resolution.ErrorCode ?? ErrorCodes.ProviderApiError,
                    resolution.ErrorMessage ?? "The platform did not provide a publishing target.",
                    resolution.Retryable, cancellationToken);
                return false;
            }

            destination.ApplyResolvedTarget(resolution.IngestUrl, secretProtector.Protect(resolution.StreamKey),
                resolution.ExternalBroadcastId, resolution.WatchUrl, clock.UtcNow, correlation.CorrelationId);

            var result = await relay.StartAsync(new RelayStartRequest(
                destination.Id,
                session.Id,
                session.MediaPathName,
                sourceCredential,
                resolution.IngestUrl,
                resolution.StreamKey,
                destination.Provider.ToString()), cancellationToken);

            if (!result.Accepted)
            {
                await HandleFailureAsync(destination, ErrorCodes.RelayUnavailable,
                    result.FailureReason ?? "The relay refused the destination.", retryable: true, cancellationToken);
                return false;
            }

            destination.TransitionTo(DestinationStatus.Connecting, clock.UtcNow,
                reason: "Connecting to the platform.", correlationId: correlation.CorrelationId);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Starting destination {DestinationId} failed", destination.Id);

            await HandleFailureAsync(destination, ErrorCodes.DestinationUnavailable,
                "The destination could not be started.", retryable: true, cancellationToken);

            return false;
        }
    }

    private async Task RetryAsync(StreamDestination destination, CancellationToken cancellationToken)
    {
        var session = destination.LiveSession
                      ?? await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == destination.LiveSessionId,
                          cancellationToken);

        if (session is null || !LiveSessionStateMachine.IsBroadcasting(session.Status))
        {
            await StopOneAsync(destination, "Session is no longer broadcasting.", cancellationToken);
            return;
        }

        var sourceCredential = await credentials.IssueRelayReadCredentialAsync(
            session, _options.RelaySourceCredentialLifetime, cancellationToken);

        logger.LogInformation("Retrying destination {DestinationId} attempt {Attempt}",
            destination.Id, destination.AttemptCount + 1);

        await TryBeginAttemptAsync(destination, session, sourceCredential, cancellationToken);
    }

    /// <summary>
    /// Records a failure and decides between backing off and giving up.
    ///
    /// A non-retryable failure — rejected credentials, an ineligible account — goes straight to
    /// ERROR. Retrying those only burns the retry budget and risks the platform rate-limiting the
    /// account.
    /// </summary>
    private async Task HandleFailureAsync(StreamDestination destination, string errorCode, string message,
        bool retryable, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // Tear the relay down before deciding what happens next, so a retry starts from a clean
        // process rather than racing a half-dead one.
        await TryStopRelayAsync(destination.Id, cancellationToken);

        if (!retryable)
        {
            destination.TransitionTo(DestinationStatus.Error, now, reason: message, errorCode: errorCode,
                correlationId: correlation.CorrelationId);

            logger.LogWarning(
                "Destination {DestinationId} failed permanently: {ErrorCode} provider={Provider}",
                destination.Id, errorCode, destination.Provider);

            return;
        }

        if (destination.AttemptCount >= _options.MaxRetryAttempts)
        {
            destination.ExhaustRetries(now, errorCode, message, correlation.CorrelationId);

            logger.LogWarning(
                "Destination {DestinationId} gave up after {Attempts} attempts: {ErrorCode}",
                destination.Id, destination.AttemptCount, errorCode);

            return;
        }

        var delay = _options.RetryDelayForAttempt(destination.AttemptCount + 1);
        destination.ScheduleRetry(now, delay, errorCode, message, correlation.CorrelationId);

        logger.LogInformation(
            "Destination {DestinationId} retrying in {DelaySeconds}s after {ErrorCode}",
            destination.Id, (int)delay.TotalSeconds, errorCode);
    }

    private async Task StopOneAsync(StreamDestination destination, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (destination.Status is DestinationStatus.Stopped or DestinationStatus.Disabled
            or DestinationStatus.Idle)
        {
            return;
        }

        if (DestinationStateMachine.CanTransition(destination.Status, DestinationStatus.Stopping))
        {
            destination.TransitionTo(DestinationStatus.Stopping, now, reason: reason,
                correlationId: correlation.CorrelationId);
        }

        await TryStopRelayAsync(destination.Id, cancellationToken);
        await NotifyProviderEndedAsync(destination, cancellationToken);

        // Clearing the per-run key on stop is what keeps a resolved secret from outliving the
        // broadcast it was minted for.
        destination.ResetForRun(clock.UtcNow);

        if (DestinationStateMachine.CanTransition(destination.Status, DestinationStatus.Stopped))
        {
            destination.TransitionTo(DestinationStatus.Stopped, clock.UtcNow, reason: reason,
                correlationId: correlation.CorrelationId);
        }
    }

    private async Task TryStopRelayAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        try
        {
            await relay.StopAsync(destinationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The relay reaps orphans on its own; failing to stop one must not block the state change.
            logger.LogWarning(ex, "Stopping relay for destination {DestinationId} failed", destinationId);
        }
    }

    /// <summary>
    /// Builds the adapter context, decrypting exactly the one secret this destination needs.
    /// Returns null when a linked account cannot produce a usable token.
    /// </summary>
    private async Task<DestinationResolveContext?> BuildResolveContextAsync(StreamDestination destination,
        LiveSession session, CancellationToken cancellationToken)
    {
        var isPublic = session.Visibility == LiveSessionVisibility.Public;

        if (destination.CredentialMode == DestinationCredentialMode.StreamKey)
        {
            var streamKey = destination.StreamKeyCipher is null
                ? null
                : secretProtector.Unprotect(destination.StreamKeyCipher);

            return new DestinationResolveContext(destination, session.Title, session.Description, isPublic,
                streamKey, null);
        }

        var account = destination.ProviderAccount
                      ?? await db.ProviderAccounts.FirstOrDefaultAsync(
                          a => a.Id == destination.ProviderAccountId, cancellationToken);

        if (account is null)
        {
            return null;
        }

        var accessToken = await accounts.GetUsableAccessTokenAsync(account, cancellationToken);
        return accessToken is null
            ? null
            : new DestinationResolveContext(destination, session.Title, session.Description, isPublic,
                null, accessToken);
    }

    /// <summary>
    /// Tells the provider the broadcast is live. Best effort: media is already flowing, so a failure
    /// here is recorded and moved past rather than treated as a destination failure.
    /// </summary>
    private async Task NotifyProviderLiveAsync(StreamDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            var adapter = providers.Get(destination.Provider);
            var accessToken = await ResolveAccessTokenAsync(destination, cancellationToken);
            var result = await adapter.OnBroadcastLiveAsync(
                new DestinationLifecycleContext(destination, accessToken), cancellationToken);

            if (!result.Success)
            {
                destination.RecordEvent(DestinationEventType.StateChanged, clock.UtcNow,
                    detail: result.ErrorMessage, errorCode: result.ErrorCode,
                    correlationId: correlation.CorrelationId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Provider live notification failed for destination {DestinationId}", destination.Id);
        }
    }

    private async Task NotifyProviderEndedAsync(StreamDestination destination, CancellationToken cancellationToken)
    {
        if (destination.ExternalBroadcastId is null)
        {
            return;
        }

        try
        {
            var adapter = providers.Get(destination.Provider);
            var accessToken = await ResolveAccessTokenAsync(destination, cancellationToken);
            await adapter.OnBroadcastEndedAsync(
                new DestinationLifecycleContext(destination, accessToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Provider end notification failed for destination {DestinationId}", destination.Id);
        }
    }

    private async Task<string?> ResolveAccessTokenAsync(StreamDestination destination,
        CancellationToken cancellationToken)
    {
        if (destination.CredentialMode != DestinationCredentialMode.LinkedAccount)
        {
            return null;
        }

        var account = destination.ProviderAccount
                      ?? await db.ProviderAccounts.FirstOrDefaultAsync(
                          a => a.Id == destination.ProviderAccountId, cancellationToken);

        return account is null ? null : await accounts.GetUsableAccessTokenAsync(account, cancellationToken);
    }

    /// <summary>
    /// Whether a relay error is worth another attempt. Unknown codes are treated as retryable: an
    /// unnecessary retry costs a few seconds, whereas wrongly giving up ends distribution for the
    /// rest of the broadcast.
    /// </summary>
    private static bool IsRetryable(string? errorCode) => errorCode switch
    {
        ErrorCodes.DestinationRejected => false,
        ErrorCodes.DestinationAuthFailed => false,
        ErrorCodes.ProviderQuotaExceeded => false,
        _ => true,
    };

    private async Task<(LiveSession Session, StreamDestination Destination)> LoadForOperatorAsync(Guid sessionId,
        Guid destinationId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                      ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        var destination = await db.StreamDestinations
                              .Include(d => d.ProviderAccount)
                              .FirstOrDefaultAsync(d => d.Id == destinationId, cancellationToken)
                          ?? throw new DomainException(ErrorCodes.SessionNotFound, "Destination not found.");

        if (destination.LiveSessionId != sessionId)
        {
            throw new DomainException(ErrorCodes.SessionNotFound, "Destination not found.");
        }

        return (session, destination);
    }

    private async Task PublishAllAsync(IEnumerable<StreamDestination> destinations, CancellationToken cancellationToken)
    {
        foreach (var destination in destinations)
        {
            await PublishAsync(destination, cancellationToken);
        }
    }

    private async Task PublishAsync(StreamDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.DestinationStateChangedAsync(
                DestinationMapper.ToStatusResponse(destination, clock.UtcNow), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Realtime is a latency optimisation; polling still reflects the truth.
            logger.LogWarning(ex, "Publishing destination state for {DestinationId} failed", destination.Id);
        }
    }
}

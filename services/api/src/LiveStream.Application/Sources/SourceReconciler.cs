using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Sources.Contracts;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Sources;

/// <summary>
/// Reconciles each contributing source against what the media plane actually reports.
///
/// This is what "expose their source/status to the control room" means in practice: presence is
/// observed from the gateway rather than declared by the device, so a phone that dies silently
/// still shows as disconnected within a poll interval.
///
/// Deliberately separate from <see cref="LiveStream.Application.Sessions.LiveSessionReconciler"/>:
/// a source is not a session, and a fault in one loop must not stop the other. Nothing here can
/// move session state.
/// </summary>
public sealed class SourceReconciler(
    IAppDbContext db,
    IMediaGateway mediaGateway,
    ILiveSessionNotifier notifier,
    IClock clock,
    IOptions<SourceOptions> options,
    ILogger<SourceReconciler> logger)
{
    private readonly SourceOptions _options = options.Value;

    /// <summary>Session states during which contributing devices are worth polling.</summary>
    private static readonly LiveSessionStatus[] ActiveSessionStatuses =
    [
        LiveSessionStatus.Ready,
        LiveSessionStatus.Starting,
        LiveSessionStatus.Live,
        LiveSessionStatus.Degraded,
        LiveSessionStatus.Reconnecting,
    ];

    public async Task<int> ReconcileActiveSourcesAsync(CancellationToken cancellationToken)
    {
        var trackedStatuses = SourceStateMachine.ActiveStates.ToArray();

        var sources = await db.SessionSources
            .Include(s => s.LiveSession)
            .Where(s => trackedStatuses.Contains(s.Status)
                        && s.MediaPathName != string.Empty
                        && s.LiveSession != null
                        && ActiveSessionStatuses.Contains(s.LiveSession.Status))
            .ToListAsync(cancellationToken);

        if (sources.Count == 0)
        {
            return 0;
        }

        var changed = new List<SessionSource>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await ReconcileOneAsync(source, cancellationToken))
                {
                    changed.Add(source);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable source must never stop the others from being serviced.
                logger.LogError(ex, "Reconciling source {SourceId} failed", source.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var source in changed)
        {
            await SafeNotifyAsync(source, cancellationToken);
        }

        return sources.Count;
    }

    private async Task<bool> ReconcileOneAsync(SessionSource source, CancellationToken cancellationToken)
    {
        MediaPathState? state;
        try
        {
            state = await mediaGateway.GetPathStateAsync(source.MediaPathName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same rule as everywhere else: not being able to see the gateway is not evidence that
            // a device dropped. Leave the source alone and try again next tick.
            logger.LogWarning(ex, "Media gateway unreachable while reconciling source {SourceId}", source.Id);
            return false;
        }

        var now = clock.UtcNow;
        var previousStatus = source.Status;
        var previousConnected = source.IngestConnected;

        var publisherConnected = state?.PublisherConnected ?? false;
        var bytesReceived = state?.BytesReceived ?? source.BytesReceived;

        // A short grace period before declaring a device gone, for the same reason sessions have a
        // recovery window: a phone switching between cell and wifi has not left the show.
        if (!publisherConnected
            && source.Status is SourceStatus.Connected
            && source.LastSeenAt is { } lastSeen
            && now - lastSeen < _options.SourcePresenceGrace)
        {
            return false;
        }

        source.ObserveMedia(publisherConnected, CalculateBitrateKbps(source, bytesReceived, now), bytesReceived, now);

        return source.Status != previousStatus || source.IngestConnected != previousConnected;
    }

    /// <summary>
    /// Bitrate from the byte delta since the last observation.
    ///
    /// Returns null on the first observation, when there is no previous point to measure from —
    /// better an empty reading than a fabricated one computed against a zero baseline.
    /// </summary>
    private static int? CalculateBitrateKbps(SessionSource source, long bytesReceived, DateTimeOffset now)
    {
        if (source.LastSeenAt is not { } lastSeen || bytesReceived <= source.BytesReceived)
        {
            return null;
        }

        var elapsed = (now - lastSeen).TotalSeconds;
        if (elapsed <= 0)
        {
            return null;
        }

        var deltaBits = (bytesReceived - source.BytesReceived) * 8d;
        return (int)Math.Round(deltaBits / elapsed / 1000d);
    }

    private async Task SafeNotifyAsync(SessionSource source, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.SourceStateChangedAsync(
                SourceMapper.ToStatusResponse(source, clock.UtcNow), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Publishing source state for {SourceId} failed", source.Id);
        }
    }
}

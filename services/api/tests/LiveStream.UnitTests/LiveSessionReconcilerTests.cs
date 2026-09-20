using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Recordings;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using LiveStream.Infrastructure.Persistence;
using LiveStream.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Tests for the reliability behaviour the product promises: a dropped connection becomes
/// RECONNECTING rather than ENDED, recovery is bounded, and quality degradation is visible without
/// dropping the broadcast (MASTER_BLUEPRINT.md §11, docs/03-streaming-engine.md).
/// </summary>
public class LiveSessionReconcilerTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeMediaGateway _media = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeRecordingStore _recordingStore = new();
    private readonly TestClock _clock = new();

    private readonly LiveSessionOptions _options = new()
    {
        RecoveryWindowSeconds = 120,
        StartIngestTimeoutSeconds = 60,
        HealthyBitrateKbps = 1200,
        PoorBitrateKbps = 400,
    };

    // -----------------------------------------------------------------------------------------
    // STARTING
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_becomes_live_once_the_media_plane_reports_a_publisher()
    {
        var session = await SeedAsync(LiveSessionStatus.Starting);
        _media.ConnectPublisher(session.MediaPathName);

        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Live, await StatusAsync(session.Id));
        Assert.Contains(_notifier.StateChanges, s => s.Status == "LIVE");
    }

    [Fact]
    public async Task Starting_stays_pending_while_inside_the_start_timeout()
    {
        var session = await SeedAsync(LiveSessionStatus.Starting);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await ReconcileAsync();

        // The broadcaster may still be negotiating its transport; do not fail the start early.
        Assert.Equal(LiveSessionStatus.Starting, await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Starting_fails_when_no_media_arrives_within_the_timeout()
    {
        var session = await SeedAsync(LiveSessionStatus.Starting);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Failed, await StatusAsync(session.Id));

        await using var context = _database.CreateContext();
        var reloaded = await context.LiveSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(ErrorCodes.StreamStartFailed, reloaded.LastErrorCode);
    }

    // -----------------------------------------------------------------------------------------
    // Reconnect
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Losing_ingest_moves_a_live_session_to_reconnecting_not_ended()
    {
        // This is the single most important reliability rule in the product.
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.DisconnectPublisher(session.MediaPathName);

        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Reconnecting, await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Reconnecting_survives_the_whole_recovery_window()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.DisconnectPublisher(session.MediaPathName);
        await ReconcileAsync();

        for (var elapsed = 0; elapsed < 110; elapsed += 10)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            await ReconcileAsync();
            Assert.Equal(LiveSessionStatus.Reconnecting, await StatusAsync(session.Id));
        }
    }

    [Fact]
    public async Task Reconnecting_returns_to_live_when_the_broadcaster_comes_back()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.DisconnectPublisher(session.MediaPathName);
        await ReconcileAsync();

        _clock.Advance(TimeSpan.FromSeconds(20));
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 5_000_000);
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Live, await StatusAsync(session.Id));

        await using var context = _database.CreateContext();
        var events = await context.LiveSessionEvents.Where(e => e.LiveSessionId == session.Id).ToListAsync();
        Assert.Contains(events, e => e.Type is LiveSessionEventType.ReconnectSucceeded);
    }

    [Fact]
    public async Task Reconnecting_fails_only_after_the_recovery_window_expires()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.DisconnectPublisher(session.MediaPathName);
        await ReconcileAsync();

        _clock.Advance(TimeSpan.FromSeconds(121));
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Failed, await StatusAsync(session.Id));

        await using var context = _database.CreateContext();
        var events = await context.LiveSessionEvents.Where(e => e.LiveSessionId == session.Id).ToListAsync();
        Assert.Contains(events, e => e.Type is LiveSessionEventType.ReconnectFailed);
    }

    [Fact]
    public async Task Reconnect_attempts_are_counted_for_the_session()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);

        for (var i = 0; i < 3; i++)
        {
            _media.DisconnectPublisher(session.MediaPathName);
            await ReconcileAsync();

            _clock.Advance(TimeSpan.FromSeconds(5));
            _media.ConnectPublisher(session.MediaPathName, bytesReceived: 1_000_000 * (i + 1));
            await ReconcileAsync();
            _clock.Advance(TimeSpan.FromSeconds(5));
        }

        await using var context = _database.CreateContext();
        var health = await context.LiveSessionHealth.SingleAsync(h => h.LiveSessionId == session.Id);
        Assert.Equal(3, health.ReconnectCount);
    }

    [Fact]
    public async Task A_gateway_outage_does_not_end_a_healthy_broadcast()
    {
        // If the control plane cannot see the media plane, that is not evidence the stream dropped.
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.ThrowOnStateLookup = true;

        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Live, await StatusAsync(session.Id));
    }

    // -----------------------------------------------------------------------------------------
    // Quality
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sustained_low_bitrate_degrades_the_session_without_dropping_it()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);

        // First pass establishes the byte-counter baseline.
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 0);
        await ReconcileAsync();

        // ~80 kbps over 10 seconds: well below the poor threshold.
        _clock.Advance(TimeSpan.FromSeconds(10));
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 100_000);
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Degraded, await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Recovering_bitrate_returns_a_degraded_session_to_live()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 0);
        await ReconcileAsync();

        _clock.Advance(TimeSpan.FromSeconds(10));
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 100_000);
        await ReconcileAsync();
        Assert.Equal(LiveSessionStatus.Degraded, await StatusAsync(session.Id));

        // ~4 Mbps over the next 10 seconds.
        _clock.Advance(TimeSpan.FromSeconds(10));
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 5_100_000);
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Live, await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Health_is_reported_as_good_at_a_healthy_bitrate()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 0);
        await ReconcileAsync();

        _clock.Advance(TimeSpan.FromSeconds(10));
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 5_000_000);
        await ReconcileAsync();

        await using var context = _database.CreateContext();
        var health = await context.LiveSessionHealth.SingleAsync(h => h.LiveSessionId == session.Id);
        Assert.Equal(StreamHealthStatus.Good, health.Status);
        Assert.True(health.BitrateKbps > 1200);
    }

    [Fact]
    public async Task Viewer_count_changes_are_published()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.ConnectPublisher(session.MediaPathName, viewers: 7);

        await ReconcileAsync();

        Assert.Contains(_notifier.ViewerCounts, v => v.SessionId == session.Id && v.ViewerCount == 7);
    }

    // -----------------------------------------------------------------------------------------
    // Idempotency and teardown
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Repeated_reconciliation_of_an_unchanged_stream_adds_no_events()
    {
        var session = await SeedAsync(LiveSessionStatus.Live);
        _media.ConnectPublisher(session.MediaPathName, bytesReceived: 1_000_000);
        await ReconcileAsync();

        await using (var context = _database.CreateContext())
        {
            var before = await context.LiveSessionEvents.CountAsync(e => e.LiveSessionId == session.Id);

            for (var i = 0; i < 5; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(3));
                _media.ConnectPublisher(session.MediaPathName, bytesReceived: 1_000_000 + (600_000 * (i + 1)));
                await ReconcileAsync();
            }

            await using var after = _database.CreateContext();
            var afterCount = await after.LiveSessionEvents.CountAsync(e => e.LiveSessionId == session.Id);
            Assert.Equal(before, afterCount);
        }
    }

    [Fact]
    public async Task Terminal_sessions_are_left_alone()
    {
        var session = await SeedAsync(LiveSessionStatus.Ended);
        _media.ConnectPublisher(session.MediaPathName);

        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Ended, await StatusAsync(session.Id));
        Assert.Empty(_notifier.StateChanges);
    }

    [Fact]
    public async Task Failing_a_session_releases_media_and_finalizes_the_recording()
    {
        _recordingStore.Stored = new StoredRecording(2_500_000, 4, _clock.UtcNow, _clock.UtcNow.AddMinutes(3));
        var session = await SeedAsync(LiveSessionStatus.Live, recordingEnabled: true);

        _media.DisconnectPublisher(session.MediaPathName);
        await ReconcileAsync();

        _clock.Advance(TimeSpan.FromSeconds(121));
        await ReconcileAsync();

        Assert.Equal(LiveSessionStatus.Failed, await StatusAsync(session.Id));
        Assert.Contains(session.MediaPathName, _media.ReleasedPaths);

        await using var context = _database.CreateContext();
        var recording = await context.Recordings.SingleAsync(r => r.LiveSessionId == session.Id);
        Assert.Equal(RecordingStatus.Ready, recording.Status);
        Assert.Equal(2_500_000, recording.SizeBytes);
    }

    // -----------------------------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------------------------

    private async Task<LiveSession> SeedAsync(LiveSessionStatus status, bool recordingEnabled = false)
    {
        await using var context = _database.CreateContext();

        var session = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Test session", null,
            LiveSessionVisibility.Public, recordingEnabled, _clock.UtcNow);

        foreach (var step in PathTo(status))
        {
            session.TransitionTo(step, _clock.UtcNow);
        }

        if (recordingEnabled)
        {
            session.AddRecording(new Recording
            {
                StorageKey = session.MediaPathName,
                Status = RecordingStatus.Recording,
                StartedAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
            });
        }

        context.LiveSessions.Add(session);
        await context.SaveChangesAsync();

        await _media.ProvisionPathAsync(
            new MediaPathRequest(session.Id, session.MediaPathName, recordingEnabled),
            CancellationToken.None);

        return session;
    }

    /// <summary>Legal transition sequence from DRAFT to the requested status.</summary>
    private static IEnumerable<LiveSessionStatus> PathTo(LiveSessionStatus status) => status switch
    {
        LiveSessionStatus.Draft => [],
        LiveSessionStatus.Preparing => [LiveSessionStatus.Preparing],
        LiveSessionStatus.Ready => [LiveSessionStatus.Preparing, LiveSessionStatus.Ready],
        LiveSessionStatus.Starting =>
            [LiveSessionStatus.Preparing, LiveSessionStatus.Ready, LiveSessionStatus.Starting],
        LiveSessionStatus.Live =>
            [LiveSessionStatus.Preparing, LiveSessionStatus.Ready, LiveSessionStatus.Starting, LiveSessionStatus.Live],
        LiveSessionStatus.Ended =>
        [
            LiveSessionStatus.Preparing, LiveSessionStatus.Ready, LiveSessionStatus.Starting,
            LiveSessionStatus.Live, LiveSessionStatus.Stopping, LiveSessionStatus.Ended,
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported seed state."),
    };

    private async Task ReconcileAsync()
    {
        await using var context = _database.CreateContext();

        var recordingService = new RecordingService(context, _recordingStore,
            TestGovernance.Limits(context, _clock), _notifier, _clock,
            NullLogger<RecordingService>.Instance);

        // These tests are about session lifecycle rules, so distribution is stubbed out entirely.
        // Destination behaviour has its own suite.
        var reconciler = new LiveSessionReconciler(context, _media, recordingService, _notifier,
            new NullDistributionCoordinator(), _clock,
            Options.Create(_options), NullLogger<LiveSessionReconciler>.Instance);

        await reconciler.ReconcileActiveSessionsAsync(CancellationToken.None);
    }

    private async Task<LiveSessionStatus> StatusAsync(Guid sessionId)
    {
        await using var context = _database.CreateContext();
        return await context.LiveSessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync();
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();
}

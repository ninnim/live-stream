using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The Phase 1 acceptance journey end to end over HTTP:
/// create → prepare → credential → start → LIVE → health → stop → ENDED → recording finalized
/// (implementation/phase-1-native-web-broadcasting.md, docs/14-testing.md).
/// </summary>
public class LiveSessionLifecycleTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task The_full_broadcast_journey_completes()
    {
        factory.RecordingStore.Stored = new Application.Abstractions.StoredRecording(
            8_400_000, 6, factory.Clock.UtcNow, factory.Clock.UtcNow.AddMinutes(4));

        var owner = await factory.CreateAuthenticatedClientAsync();

        // 1. Create -------------------------------------------------------------------------
        var created = await CreateSessionAsync(owner, recordingEnabled: true);
        Assert.Equal("DRAFT", created.Status);
        Assert.False(created.Playback!.IsLive);

        // 2. Prepare ------------------------------------------------------------------------
        var prepared = await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{created.Id}/prepare");
        Assert.Equal("READY", prepared.Status);

        // 3. Issue a short-lived broadcaster credential --------------------------------------
        var credential = await PostAsync<IngestCredentialResponse>(owner,
            $"/api/v1/live-sessions/{created.Id}/sources/credentials");

        Assert.Equal("WHIP", credential.Protocol);
        Assert.Contains("/whip", credential.IngestUrl, StringComparison.Ordinal);
        Assert.NotEmpty(credential.Token);
        Assert.True(credential.ExpiresAt > factory.Clock.UtcNow);

        // 4. Browser publishes, then start ---------------------------------------------------
        var mediaPath = await MediaPathAsync(created.Id);
        factory.Media.ConnectPublisher(mediaPath, bytesReceived: 2_000_000, viewers: 3);

        var started = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{created.Id}/start");
        Assert.Equal("LIVE", started.Status);
        Assert.True(started.IsBroadcasting);
        Assert.NotNull(started.StartedAt);

        // 5. Health is visible to the studio -------------------------------------------------
        var health = await GetAsync<LiveSessionHealthResponse>(owner, $"/api/v1/live-sessions/{created.Id}/health");
        Assert.True(health.IngestConnected);

        // 6. A viewer can resolve playback ---------------------------------------------------
        var playback = await GetAsync<PlaybackResponse>(owner, $"/api/v1/live-sessions/{created.Id}/playback");
        Assert.True(playback.IsLive);
        Assert.Contains("index.m3u8", playback.HlsUrl, StringComparison.Ordinal);

        // 7. Stop ----------------------------------------------------------------------------
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        var stopped = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{created.Id}/stop");

        Assert.Equal("ENDED", stopped.Status);
        Assert.True(stopped.IsTerminal);
        Assert.Equal(240, stopped.DurationSeconds);

        // 8. Recording metadata is finalized --------------------------------------------------
        var recordings = await GetAsync<List<RecordingResponse>>(owner,
            $"/api/v1/live-sessions/{created.Id}/recordings");

        var recording = Assert.Single(recordings);
        Assert.Equal("READY", recording.Status);
        Assert.Equal(8_400_000, recording.SizeBytes);

        // 9. Media resources were released and credentials revoked ---------------------------
        Assert.Contains(mediaPath, factory.Media.ReleasedPaths);

        await using var db = factory.CreateDbContext();
        var credentials = await db.IngestCredentials.Where(c => c.LiveSessionId == created.Id).ToListAsync();
        Assert.All(credentials, c => Assert.NotNull(c.RevokedAt));
    }

    [Fact]
    public async Task Every_state_transition_is_written_to_the_event_log()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id));
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        var events = await GetAsync<List<LiveSessionEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/events");

        var transitions = events.Where(e => e.ToStatus is not null).Select(e => e.ToStatus).ToList();
        Assert.Contains("PREPARING", transitions);
        Assert.Contains("READY", transitions);
        Assert.Contains("STARTING", transitions);
        Assert.Contains("LIVE", transitions);
        Assert.Contains("STOPPING", transitions);
        Assert.Contains("ENDED", transitions);
    }

    [Fact]
    public async Task Starting_without_ingest_leaves_the_session_pending_rather_than_live()
    {
        // The studio must not display LIVE until the media pipeline is actually carrying media
        // (docs/04-native-broadcasting.md safety behaviour).
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        var started = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

        Assert.Equal("STARTING", started.Status);
        Assert.False(started.IsBroadcasting);
    }

    [Fact]
    public async Task Starting_before_preparing_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LIVE_002_SESSION_NOT_READY", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Stopping_a_draft_session_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/stop", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Prepare_is_idempotent()
    {
        // A studio reload re-runs prepare; that must not corrupt session state.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var first = await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        var second = await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        Assert.Equal("READY", first.Status);
        Assert.Equal("READY", second.Status);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task Stop_is_idempotent()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id));
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

        var first = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");
        var second = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        Assert.Equal("ENDED", first.Status);
        Assert.Equal("ENDED", second.Status);
    }

    [Fact]
    public async Task Prepare_fails_cleanly_when_the_media_plane_is_unavailable()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        factory.Media.ProvisionFailureReason = "Media gateway is unreachable.";
        try
        {
            var response = await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("LIVE_012_MEDIA_GATEWAY_UNAVAILABLE", await ErrorCodeAsync(response));
        }
        finally
        {
            factory.Media.ProvisionFailureReason = null;
        }

        // The session lands in FAILED rather than being left stuck in PREPARING.
        var status = await GetAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/status");
        Assert.Equal("FAILED", status.Status);
    }

    [Fact]
    public async Task Recording_is_marked_failed_when_no_media_was_captured()
    {
        // A recording failure must not affect the live session outcome.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, recordingEnabled: true);

        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id));
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

        factory.RecordingStore.Stored = null;
        var stopped = await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        Assert.Equal("ENDED", stopped.Status);

        await using var db = factory.CreateDbContext();
        var recording = await db.Recordings.SingleAsync(r => r.LiveSessionId == session.Id);
        Assert.Equal(RecordingStatus.Failed, recording.Status);
    }

    [Fact]
    public async Task Sessions_without_recording_enabled_produce_no_recording_rows()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, recordingEnabled: false);

        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");
        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id));
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        var recordings = await GetAsync<List<RecordingResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/recordings");

        Assert.Empty(recordings);
    }

    [Fact]
    public async Task Status_reports_the_transitions_currently_allowed()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var status = await GetAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/status");

        Assert.Equal("DRAFT", status.Status);
        Assert.Contains("PREPARING", status.AllowedTransitions);
        Assert.DoesNotContain("LIVE", status.AllowedTransitions);
    }

    [Fact]
    public async Task Broadcaster_signals_are_logged_but_do_not_change_state()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{session.Id}/broadcaster-signals",
            new BroadcasterSignalRequest("RECONNECT_ATTEMPT", "ICE connection failed"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await GetAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/status");
        Assert.Equal("READY", status.Status);

        var events = await GetAsync<List<LiveSessionEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/events");
        Assert.Contains(events, e => e.Type == "ReconnectAttempted");
    }

    [Fact]
    public async Task An_unknown_broadcaster_signal_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{session.Id}/broadcaster-signals",
            new BroadcasterSignalRequest("DROP_TABLE", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client,
        bool recordingEnabled = false, string visibility = "PUBLIC")
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", "Integration test session", visibility,
                recordingEnabled));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task<T> PostAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.PostAsync(url, null);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.GetAsync(url);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem?.TryGetValue("errorCode", out var code) == true ? code.ToString() : null;
    }

    private async Task<string> MediaPathAsync(Guid sessionId)
    {
        await using var db = factory.CreateDbContext();
        return await db.LiveSessions.Where(s => s.Id == sessionId).Select(s => s.MediaPathName).SingleAsync();
    }
}

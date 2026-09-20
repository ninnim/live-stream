using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Distribution;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Distribution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The Phase 2 acceptance criteria, exercised over HTTP against the real API
/// (implementation/phase-2-multi-platform-distribution.md):
///
/// <list type="bullet">
/// <item>Core stream can remain LIVE if one destination fails.</item>
/// <item>Destination status is visible independently.</item>
/// <item>Credentials never appear in frontend responses.</item>
/// <item>Transient publishing failures retry.</item>
/// <item>Provider-specific errors are normalized into stable internal error codes.</item>
/// </list>
///
/// Only the relay and the media gateway are substituted; every authorization, state, encryption and
/// persistence decision runs for real.
/// </summary>
public class DestinationTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_destination_can_be_added_listed_and_removed()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var created = await AddDestinationAsync(owner, session.Id, "Partner CDN");

        Assert.Equal("CustomRtmp", created.Provider);
        Assert.Equal("Partner CDN", created.DisplayName);
        Assert.Equal("StreamKey", created.CredentialMode);
        Assert.Equal("IDLE", created.Status.ToUpperInvariant());
        Assert.True(created.Enabled);
        Assert.True(created.HasStreamKey);

        var listed = await GetAsync<List<DestinationResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations");
        Assert.Single(listed);

        var deleted = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/destinations/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        listed = await GetAsync<List<DestinationResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations");
        Assert.Empty(listed);
    }

    /// <summary>
    /// "Credentials never appear in frontend responses." Asserted against the raw JSON rather than
    /// the typed contract, because a leak would most likely come from a field being added to the
    /// response shape — which a typed assertion would never notice.
    /// </summary>
    [Fact]
    public async Task The_stream_key_never_appears_in_any_response()
    {
        const string secretKey = "super-secret-key-value-9d3f";

        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var created = await AddDestinationAsync(owner, session.Id, "Partner", streamKey: secretKey);

        string[] urls =
        [
            $"/api/v1/live-sessions/{session.Id}/destinations",
            $"/api/v1/live-sessions/{session.Id}/destinations/{created.Id}",
            $"/api/v1/live-sessions/{session.Id}/destinations/{created.Id}/events",
            $"/api/v1/live-sessions/{session.Id}",
        ];

        foreach (var url in urls)
        {
            var body = await owner.Client.GetStringAsync(url);
            Assert.DoesNotContain(secretKey, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The key is encrypted at rest, so the database column must not contain it either.</summary>
    [Fact]
    public async Task The_stream_key_is_encrypted_in_the_database()
    {
        const string secretKey = "at-rest-secret-4a71";

        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var created = await AddDestinationAsync(owner, session.Id, "Partner", streamKey: secretKey);

        await using var db = factory.CreateDbContext();
        var stored = await db.StreamDestinations.SingleAsync(d => d.Id == created.Id);

        Assert.NotNull(stored.StreamKeyCipher);
        Assert.DoesNotContain(secretKey, stored.StreamKeyCipher);
        Assert.StartsWith("v1:", stored.StreamKeyCipher);

        // ... and it is genuinely recoverable, not merely mangled.
        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(secretKey, protector.Unprotect(stored.StreamKeyCipher));
    }

    [Theory]
    [InlineData("http://example.test/live")]
    [InlineData("file:///tmp/out.flv")]
    [InlineData("srt://example.test:9000")]
    public async Task Non_rtmp_ingest_urls_are_rejected(string ingestUrl)
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/destinations",
            new CreateDestinationRequest("CustomRtmp", "Bad", ingestUrl, "key", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Supplying_both_a_stream_key_and_an_account_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/destinations",
            new CreateDestinationRequest("YouTube", "Both", "rtmp://a.test/app", "key", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Supplying_neither_a_stream_key_nor_an_account_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/destinations",
            new CreateDestinationRequest("CustomRtmp", "Neither", "rtmp://a.test/app", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_destination_limit_is_enforced()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        // The factory configures a limit of five.
        for (var i = 0; i < 5; i++)
        {
            await AddDestinationAsync(owner, session.Id, $"Destination {i}");
        }

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/destinations",
            new CreateDestinationRequest("CustomRtmp", "One too many", "rtmp://a.test/app", "key", null));

        Assert.Equal("LIVE_022_DESTINATION_LIMIT_REACHED", await ErrorCodeAsync(response));
    }

    /// <summary>
    /// A destination id from another session must not be operable through a session the caller can
    /// reach. Without the parent check this is a cross-tenant write.
    /// </summary>
    [Fact]
    public async Task A_destination_cannot_be_reached_through_a_different_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var sessionA = await CreateSessionAsync(owner);
        var sessionB = await CreateSessionAsync(owner);

        var destination = await AddDestinationAsync(owner, sessionA.Id, "A");

        var response = await owner.Client.GetAsync(
            $"/api/v1/live-sessions/{sessionB.Id}/destinations/{destination.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_workspace_cannot_see_or_change_destinations()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Private");

        var read = await stranger.Client.GetAsync($"/api/v1/live-sessions/{session.Id}/destinations");
        var write = await stranger.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/v1/live-sessions/{session.Id}/destinations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Going_live_starts_every_enabled_destination()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var enabled = await AddDestinationAsync(owner, session.Id, "Enabled");
        var disabled = await AddDestinationAsync(owner, session.Id, "Disabled");

        await SetEnabledAsync(owner, session.Id, disabled.Id, false);
        await GoLiveAsync(owner, session.Id);

        var destinations = await GetAsync<List<DestinationResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations");

        var startedDestination = destinations.Single(d => d.Id == enabled.Id);
        var skipped = destinations.Single(d => d.Id == disabled.Id);

        Assert.Equal("CONNECTING", startedDestination.Status.ToUpperInvariant());
        Assert.Equal("DISABLED", skipped.Status.ToUpperInvariant());

        // The relay was told about exactly one destination, with the target split from the key.
        var request = Assert.Single(factory.Relay.StartRequests, r => r.DestinationId == enabled.Id);
        Assert.Equal("rtmp://live.example.test/app", request.TargetUrl);
        Assert.NotEmpty(request.TargetStreamKey);
        Assert.NotEmpty(request.SourceCredential);
    }

    [Fact]
    public async Task A_connected_relay_moves_the_destination_to_live()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkConnected(destination.Id, bytesSent: 12_345);
        await ReconcileDestinationsAsync();

        var refreshed = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("LIVE", refreshed.Status.ToUpperInvariant());
        Assert.Equal(12_345, refreshed.BytesSent);
        Assert.NotNull(refreshed.StartedAt);
    }

    /// <summary>
    /// The central Phase 2 guarantee: <i>"Core stream can remain LIVE if one destination fails."</i>
    /// One platform is rejected outright while another stays connected, and the session is untouched.
    /// </summary>
    [Fact]
    public async Task A_failing_destination_does_not_affect_the_session_or_its_siblings()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var healthy = await AddDestinationAsync(owner, session.Id, "Healthy");
        var broken = await AddDestinationAsync(owner, session.Id, "Broken");

        await GoLiveAsync(owner, session.Id);

        factory.Relay.MarkConnected(healthy.Id);
        factory.Relay.MarkFailed(broken.Id, "LIVE_016_DESTINATION_REJECTED", "Invalid stream key.");
        await ReconcileDestinationsAsync();

        var destinations = await GetAsync<List<DestinationResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations");

        Assert.Equal("LIVE", destinations.Single(d => d.Id == healthy.Id).Status.ToUpperInvariant());

        var failed = destinations.Single(d => d.Id == broken.Id);
        Assert.Equal("ERROR", failed.Status.ToUpperInvariant());
        Assert.Equal("LIVE_016_DESTINATION_REJECTED", failed.LastErrorCode);

        // The session itself never noticed.
        var status = await GetAsync<LiveSessionStatusResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/status");

        Assert.Equal("LIVE", status.Status);
        Assert.True(status.IsBroadcasting);
        Assert.Null(status.LastErrorCode);
    }

    /// <summary>A rejected credential is not retried: retrying it only risks a platform rate limit.</summary>
    [Fact]
    public async Task A_rejected_destination_goes_straight_to_error_without_retrying()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Rejected");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkFailed(destination.Id, "LIVE_016_DESTINATION_REJECTED", "Invalid stream key.");
        await ReconcileDestinationsAsync();

        var refreshed = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("ERROR", refreshed.Status.ToUpperInvariant());
        Assert.Equal(0, refreshed.AttemptCount);
        Assert.Null(refreshed.NextRetryAt);
    }

    /// <summary>"Transient publishing failures retry." A dropped connection backs off and tries again.</summary>
    [Fact]
    public async Task A_transient_failure_schedules_a_retry_with_backoff()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Flaky");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkFailed(destination.Id, "LIVE_017_DESTINATION_UNAVAILABLE", "Connection reset.");
        await ReconcileDestinationsAsync();

        var retrying = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("RETRYING", retrying.Status.ToUpperInvariant());
        Assert.Equal(1, retrying.AttemptCount);
        Assert.NotNull(retrying.NextRetryAt);

        // Before the delay elapses, nothing happens.
        await ReconcileDestinationsAsync();
        var stillWaiting = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");
        Assert.Equal("RETRYING", stillWaiting.Status.ToUpperInvariant());

        // Once it does, the destination is started again.
        factory.Clock.Advance(TimeSpan.FromSeconds(10));
        await ReconcileDestinationsAsync();

        var retried = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("CONNECTING", retried.Status.ToUpperInvariant());
        Assert.True(factory.Relay.StartRequests.Count(r => r.DestinationId == destination.Id) >= 2);
    }

    /// <summary>The retry budget is bounded, so a permanently dead endpoint eventually stops.</summary>
    [Fact]
    public async Task Retries_are_bounded_and_end_in_error()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Dead");

        await GoLiveAsync(owner, session.Id);

        // The factory configures three attempts.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            factory.Relay.MarkFailed(destination.Id, "LIVE_017_DESTINATION_UNAVAILABLE", "Connection reset.");
            await ReconcileDestinationsAsync();
            factory.Clock.Advance(TimeSpan.FromMinutes(5));
            await ReconcileDestinationsAsync();
        }

        var exhausted = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("ERROR", exhausted.Status.ToUpperInvariant());
        Assert.Equal("LIVE_017_DESTINATION_UNAVAILABLE", exhausted.LastErrorCode);

        var events = await GetAsync<List<DestinationEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}/events");
        Assert.Contains(events, e => e.Type == "RetryExhausted");

        // And the session is still perfectly healthy.
        var status = await GetAsync<LiveSessionStatusResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/status");
        Assert.Equal("LIVE", status.Status);
    }

    [Fact]
    public async Task Stopping_the_session_stops_its_destinations()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkConnected(destination.Id);
        await ReconcileDestinationsAsync();

        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        var stopped = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("STOPPED", stopped.Status.ToUpperInvariant());
        Assert.Contains(destination.Id, factory.Relay.StopRequests);

        // The per-run key must not outlive the broadcast it was minted for.
        await using var db = factory.CreateDbContext();
        var stored = await db.StreamDestinations.SingleAsync(d => d.Id == destination.Id);
        Assert.Null(stored.ResolvedStreamKeyCipher);
    }

    /// <summary>
    /// An unreachable relay is not evidence that destinations dropped, exactly as an unreachable
    /// media gateway is not evidence that a broadcaster dropped. State must be left alone.
    /// </summary>
    [Fact]
    public async Task An_unreachable_relay_leaves_destination_state_untouched()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkConnected(destination.Id);
        await ReconcileDestinationsAsync();

        factory.Relay.ThrowOnAnyCall = new HttpRequestException("relay is down");
        try
        {
            await ReconcileDestinationsAsync();
        }
        finally
        {
            factory.Relay.ThrowOnAnyCall = null;
        }

        var unchanged = await GetAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal("LIVE", unchanged.Status.ToUpperInvariant());
        Assert.Null(unchanged.LastErrorCode);
    }

    [Fact]
    public async Task An_operator_can_stop_and_restart_one_destination_while_live()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkConnected(destination.Id);
        await ReconcileDestinationsAsync();

        var stopped = await PostAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}/stop");
        Assert.Equal("STOPPED", stopped.Status.ToUpperInvariant());

        var restarted = await PostAsync<DestinationResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}/start");
        Assert.Equal("CONNECTING", restarted.Status.ToUpperInvariant());

        // The session never moved.
        var status = await GetAsync<LiveSessionStatusResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/status");
        Assert.Equal("LIVE", status.Status);
    }

    [Fact]
    public async Task A_destination_cannot_be_removed_while_it_is_running()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);

        var response = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>"Destination status is visible independently" — including its own audit trail.</summary>
    [Fact]
    public async Task Destination_events_record_the_lifecycle()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var destination = await AddDestinationAsync(owner, session.Id, "Partner");

        await GoLiveAsync(owner, session.Id);
        factory.Relay.MarkConnected(destination.Id);
        await ReconcileDestinationsAsync();

        var events = await GetAsync<List<DestinationEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/destinations/{destination.Id}/events");

        Assert.Contains(events, e => e.Type == "Created");
        Assert.Contains(events, e => e.Type == "TargetResolved");
        Assert.Contains(events, e => e.ToStatus == "Live");
    }

    // -----------------------------------------------------------------------------------------
    // Provider catalogue
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_provider_catalogue_describes_every_supported_platform()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var providers = await GetAsync<List<ProviderDescriptorResponse>>(owner, "/api/v1/distribution/providers");

        var names = providers.Select(p => p.Provider).ToList();
        Assert.Contains("CustomRtmp", names);
        Assert.Contains("YouTube", names);
        Assert.Contains("Facebook", names);
        Assert.Contains("TikTok", names);
        Assert.Contains("Twitch", names);

        // No provider credentials are configured in tests, so linked accounts must report as
        // unavailable rather than offering a consent flow that cannot work.
        Assert.All(providers, p => Assert.False(p.LinkedAccountConfigured));
        Assert.All(providers, p => Assert.True(p.SupportsStreamKey));
    }

    [Fact]
    public async Task Linking_an_account_is_refused_when_the_provider_is_not_configured()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/v1/distribution/workspaces/{owner.WorkspaceId}/accounts/YouTube/authorize",
            new { redirectUri = "http://localhost:3000/oauth/callback" });

        Assert.Equal("LIVE_019_PROVIDER_ACCOUNT_UNAVAILABLE", await ErrorCodeAsync(response));
    }

    /// <summary>An unknown or replayed state value must not link anything.</summary>
    [Fact]
    public async Task An_unknown_oauth_state_is_rejected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await owner.Client.PostAsJsonAsync("/api/v1/distribution/accounts/callback",
            new CompleteProviderAuthorizationRequest("some-code", "a-state-that-was-never-issued"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Listing_accounts_in_another_workspace_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var response = await stranger.Client.GetAsync(
            $"/api/v1/distribution/workspaces/{owner.WorkspaceId}/accounts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client)
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", "Distribution test", "PUBLIC", false));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task<DestinationResponse> AddDestinationAsync(AuthenticatedClient client, Guid sessionId,
        string name, string streamKey = "test-stream-key")
    {
        var response = await client.Client.PostAsJsonAsync($"/api/v1/live-sessions/{sessionId}/destinations",
            new CreateDestinationRequest("CustomRtmp", name, "rtmp://live.example.test/app", streamKey, null));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<DestinationResponse>())!;
    }

    private async Task SetEnabledAsync(AuthenticatedClient client, Guid sessionId, Guid destinationId, bool enabled)
    {
        var response = await client.Client.PatchAsJsonAsync(
            $"/api/v1/live-sessions/{sessionId}/destinations/{destinationId}",
            new UpdateDestinationRequest(null, null, null, enabled));

        await factory.EnsureSuccessAsync(response);
    }

    /// <summary>Prepares the session, simulates the browser publishing, and starts it.</summary>
    private async Task GoLiveAsync(AuthenticatedClient client, Guid sessionId)
    {
        await PostAsync<LiveSessionResponse>(client, $"/api/v1/live-sessions/{sessionId}/prepare");

        await using (var db = factory.CreateDbContext())
        {
            var mediaPath = await db.LiveSessions.Where(s => s.Id == sessionId)
                .Select(s => s.MediaPathName).SingleAsync();
            factory.Media.ConnectPublisher(mediaPath, bytesReceived: 2_000_000, viewers: 1);
        }

        var started = await PostAsync<LiveSessionStatusResponse>(client, $"/api/v1/live-sessions/{sessionId}/start");
        Assert.Equal("LIVE", started.Status);
    }

    /// <summary>
    /// Runs one reconciliation pass directly. The hosted monitor is removed in tests so timing is
    /// deterministic — the same approach the Phase 1 health monitor tests take.
    /// </summary>
    private async Task ReconcileDestinationsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<DestinationOrchestrator>();
        await orchestrator.ReconcileActiveDestinationsAsync(CancellationToken.None);
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
}

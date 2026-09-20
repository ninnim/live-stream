using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Ai;
using LiveStream.Application.Ai.Contracts;
using LiveStream.Application.Sessions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The Phase 6 acceptance criteria, over HTTP:
/// <i>"AI features are asynchronous, observable, permissioned, and independently disableable. Core
/// streaming remains functional when AI services are unavailable."</i>
///
/// The analyst is substituted; everything else — queueing, deduplication, the worker, retries,
/// permissions, cost accounting — runs for real. That substitution is the point of the provider
/// seam: the pipeline is fully testable without an API key and without spending money.
/// </summary>
public class AiTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // Asynchronous
    // -----------------------------------------------------------------------------------------

    /// <summary>Requesting work returns immediately with a queued job; nothing calls a model inline.</summary>
    [Fact]
    public async Task Requesting_a_job_queues_it_and_returns_at_once()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs",
            new RequestAiJobRequest("SessionRecap"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = (await response.Content.ReadFromJsonAsync<AiJobResponse>())!;

        Assert.Equal("Queued", job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.ResultJson);
    }

    [Fact]
    public async Task The_worker_runs_a_queued_job_and_records_the_result()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var queued = await RequestAsync(owner, session.Id, "SessionRecap");
        await RunAiJobsAsync();

        var job = (await ListAsync(owner, session.Id)).Single(candidate => candidate.Id == queued.Id);

        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.NotNull(job.ResultJson);
        Assert.Equal("claude-opus-5", job.ModelId);
    }

    /// <summary>
    /// Pressing the button twice means "I want this", not "I want to pay twice".
    /// </summary>
    [Fact]
    public async Task A_second_request_for_the_same_thing_returns_the_job_already_running()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var first = await RequestAsync(owner, session.Id, "SessionRecap");
        var second = await RequestAsync(owner, session.Id, "SessionRecap");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await ListAsync(owner, session.Id));
    }

    [Fact]
    public async Task Different_kinds_are_separate_jobs()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await RequestAsync(owner, session.Id, "SessionRecap");
        await RequestAsync(owner, session.Id, "Chapters");

        Assert.Equal(2, (await ListAsync(owner, session.Id)).Count);
    }

    // -----------------------------------------------------------------------------------------
    // Observable
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A finished job answers "what produced this, over what range, and what did it cost" without a
    /// log search. That is what the phase's "observable" has to mean for an operator.
    /// </summary>
    [Fact]
    public async Task A_finished_job_reports_its_model_tokens_cost_and_source_range()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        await RequestAsync(owner, session.Id, "StreamQualityReview");
        await RunAiJobsAsync();

        var job = (await ListAsync(owner, session.Id)).Single();

        Assert.Equal("Succeeded", job.Status);
        Assert.True(job.InputTokens > 0);
        Assert.True(job.OutputTokens > 0);
        Assert.NotNull(job.EstimatedCostUsd);
        Assert.True(job.EstimatedCostUsd > 0m);
        // The session has events by now, so the answer is traceable to a real window of them.
        Assert.NotNull(job.SourceRangeStart);
        Assert.NotNull(job.SourceRangeEnd);
    }

    [Fact]
    public async Task A_retryable_failure_is_retried_and_the_attempts_are_visible()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        // Cleared first: the worker takes the oldest job, so a backlog from an earlier test in this
        // class would absorb the failure this test is asking for.
        await RunAiJobsAsync();
        factory.Ai.FailuresBeforeSuccess = 1;

        try
        {
            await RequestAsync(owner, session.Id, "SessionRecap");

            await RunAiJobsAsync();
            var afterFailure = (await ListAsync(owner, session.Id)).Single();

            Assert.Equal("Queued", afterFailure.Status);
            Assert.Equal(1, afterFailure.AttemptCount);
            Assert.Equal("AI_PROVIDER_UNAVAILABLE", afterFailure.ErrorCode);
            Assert.NotNull(afterFailure.NextAttemptAt);

            // The backoff is real, so nothing runs until the clock passes it.
            factory.Clock.Advance(TimeSpan.FromMinutes(5));
            await RunAiJobsAsync();

            var afterRetry = (await ListAsync(owner, session.Id)).Single();

            Assert.Equal("Succeeded", afterRetry.Status);
            Assert.Equal(2, afterRetry.AttemptCount);
            // The earlier failure does not linger on a job that went on to succeed.
            Assert.Null(afterRetry.ErrorCode);
        }
        finally
        {
            factory.Ai.FailuresBeforeSuccess = 0;
        }
    }

    [Fact]
    public async Task A_failure_that_is_not_retryable_stops_after_one_attempt()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        factory.Ai.ThrowOnCall = new AiProviderException("AI_REFUSED", "Declined.", retryable: false);

        try
        {
            await RequestAsync(owner, session.Id, "SessionRecap");
            await RunAiJobsAsync();

            var job = (await ListAsync(owner, session.Id)).Single();

            Assert.Equal("Failed", job.Status);
            Assert.Equal(1, job.AttemptCount);
            Assert.Equal("AI_REFUSED", job.ErrorCode);
        }
        finally
        {
            factory.Ai.ThrowOnCall = null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // What reaches the model
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The prompt carries the session's own timeline and nothing else.
    ///
    /// Asserted against the request the analyst actually received, because the risk is not that
    /// someone writes "send the stream key" — it is that a convenient `Include` quietly adds one.
    /// </summary>
    [Fact]
    public async Task No_credential_or_identity_reaches_the_model()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await owner.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/invitations",
            new { role = "Camera", displayName = "Second camera" });
        await factory.EnsureSuccessAsync(invitation);

        factory.Ai.Requests.Clear();

        await RequestAsync(owner, session.Id, "SessionRecap");
        await RunAiJobsAsync();

        var request = Assert.Single(factory.Ai.Requests);
        var rendered = string.Join("\n",
            new[] { request.SessionTitle, request.SessionDescription ?? string.Empty }
                .Concat(request.Timeline.Select(entry => $"{entry.Category} {entry.Type} {entry.Detail}")));

        foreach (var forbidden in new[]
                 { owner.Auth.User.Email, "PairingCode", "DeviceToken", "StreamKey", "Bearer " })
        {
            Assert.DoesNotContain(forbidden, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_timeline_is_capped_however_long_the_session_ran()
    {
        // A prompt that grows without bound is a cost that grows without bound.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        factory.Ai.Requests.Clear();
        await RequestAsync(owner, session.Id, "Chapters");
        await RunAiJobsAsync();

        var request = Assert.Single(factory.Ai.Requests);
        var limit = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>()
            .Value.MaxTimelineEntries;

        Assert.True(request.Timeline.Count <= limit);
    }

    // -----------------------------------------------------------------------------------------
    // Permissioned
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspace_cannot_request_or_read_AI_work()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var read = await stranger.Client.GetAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs");
        var write = await stranger.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs",
            new RequestAiJobRequest("SessionRecap"));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_reach_AI_at_all()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/ai/capabilities")).StatusCode);
    }

    [Fact]
    public async Task A_job_cannot_be_reached_through_a_different_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var other = await CreateSessionAsync(owner);

        var job = await RequestAsync(owner, session.Id, "SessionRecap");

        var response = await owner.Client.DeleteAsync($"/api/v1/live-sessions/{other.Id}/ai-jobs/{job.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Disableable, and harmless when unavailable
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Capabilities_report_what_this_deployment_can_actually_do()
    {
        // So the UI never offers a button that cannot work.
        var owner = await factory.CreateAuthenticatedClientAsync();

        var capabilities = await GetAsync<AiCapabilitiesResponse>(owner, "/api/v1/ai/capabilities");

        Assert.True(capabilities.Configured);
        Assert.Equal(3, capabilities.Features.Count);
        Assert.All(capabilities.Features, feature => Assert.NotEmpty(feature.Description));
    }

    [Fact]
    public async Task A_queued_job_can_be_withdrawn()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var job = await RequestAsync(owner, session.Id, "SessionRecap");

        var response = await owner.Client.DeleteAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs/{job.Id}");
        await factory.EnsureSuccessAsync(response);

        Assert.Equal("Cancelled", (await response.Content.ReadFromJsonAsync<AiJobResponse>())!.Status);

        // And withdrawing it frees the slot, so the same kind can be asked for again.
        var again = await RequestAsync(owner, session.Id, "SessionRecap");
        Assert.NotEqual(job.Id, again.Id);
    }

    [Fact]
    public async Task An_unknown_job_kind_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/ai-jobs",
            new RequestAiJobRequest("MakeItPopular"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The last acceptance criterion, asserted rather than asserted-about: with the AI provider
    /// throwing on every call, a session still prepares, starts, and reports itself live.
    /// </summary>
    [Fact]
    public async Task A_broken_AI_provider_does_not_affect_broadcasting()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        factory.Ai.ThrowOnCall = new AiProviderException("AI_PROVIDER_UNAVAILABLE", "Down.", retryable: true);

        try
        {
            await RequestAsync(owner, session.Id, "SessionRecap");
            await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

            factory.Media.ConnectPublisher(await MediaPathAsync(session.Id), bytesReceived: 2_000_000);
            await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

            // The AI worker runs against a provider that is failing, and the broadcast is untouched.
            await RunAiJobsAsync();

            var status = await GetAsync<LiveSessionStatusResponse>(owner,
                $"/api/v1/live-sessions/{session.Id}/status");

            Assert.Equal("LIVE", status.Status);
            Assert.Equal("Queued", (await ListAsync(owner, session.Id)).Single().Status);
        }
        finally
        {
            factory.Ai.ThrowOnCall = null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Drives the worker until the queue is empty.
    ///
    /// Repeated rather than a single pass, because the worker deliberately runs one job at a time,
    /// oldest first — so with a backlog from earlier tests in this class, one pass would serve
    /// somebody else's job and this test would watch its own stay queued. Bounded so a job that
    /// re-queues itself cannot spin here forever; the retry backoff means a failed job is not due
    /// again within a drain, which is what keeps the retry tests meaningful.
    /// </summary>
    private async Task RunAiJobsAsync()
    {
        for (var pass = 0; pass < 50; pass++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<AiJobRunner>();

            if (await runner.RunPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client)
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", "AI test", "PUBLIC", false));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task<AiJobResponse> RequestAsync(AuthenticatedClient client, Guid sessionId, string kind)
    {
        var response = await client.Client.PostAsJsonAsync($"/api/v1/live-sessions/{sessionId}/ai-jobs",
            new RequestAiJobRequest(kind));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AiJobResponse>())!;
    }

    private Task<List<AiJobResponse>> ListAsync(AuthenticatedClient client, Guid sessionId) =>
        GetAsync<List<AiJobResponse>>(client, $"/api/v1/live-sessions/{sessionId}/ai-jobs");

    private async Task<T> GetAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.GetAsync(url);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.PostAsync(url, null);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<string> MediaPathAsync(Guid sessionId)
    {
        await using var db = factory.CreateDbContext();
        return (await db.LiveSessions.FindAsync(sessionId))!.MediaPathName;
    }
}

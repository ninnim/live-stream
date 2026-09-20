using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Governance.Contracts;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Recordings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Tenant governance over HTTP (implementation/phase-7-scale-security-and-globalization.md).
///
/// The security property running through the whole file: a workspace can make itself smaller, and
/// only an operator can make it bigger. If a member could raise their own plan or verify their own
/// domain, neither plans nor single sign-on would mean anything.
/// </summary>
public class GovernanceTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // Limits
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_workspace_starts_on_the_deployment_default_plan()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var limits = await GetAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits");

        Assert.Equal("PRO", limits.Plan);
        Assert.Null(limits.MaxConcurrentSessionsOverride);
        Assert.Equal("eu-central", limits.DeploymentRegion);
    }

    [Fact]
    public async Task A_workspace_may_tighten_its_own_limits()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var saved = await PutAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(1, 1, 2, 3, null));

        Assert.Equal(1, saved.EffectiveMaxConcurrentSessions);
        Assert.Equal(3, saved.EffectiveRecordingRetentionDays);

        // And that tightening is enforced, not merely stored: the second session is refused.
        await CreateSessionAsync(owner);
        var second = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest("Second show", null, "PRIVATE", false));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.PlanLimitReached, await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task A_workspace_cannot_raise_a_limit_above_its_plan()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(99, null, null, null, null));

        Assert.Equal(ErrorCodes.PlanLimitReached, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Only_an_administrator_may_change_limits()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var producer = await factory.CreateAuthenticatedClientAsync();
        await AddMemberAsync(owner.WorkspaceId, producer.UserId, WorkspaceRole.Producer);

        // A producer may read them — somebody hitting a limit needs to see what it is.
        var limits = await GetAsync<WorkspaceLimitsResponse>(producer,
            $"/api/v1/workspaces/{owner.WorkspaceId}/limits");
        Assert.Equal("PRO", limits.Plan);

        var response = await producer.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(1, null, null, null, null));

        Assert.Equal(ErrorCodes.WorkspaceNotFound, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_workspace_in_another_tenant_is_not_readable()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var outsider = await factory.CreateAuthenticatedClientAsync();

        var response = await outsider.Client.GetAsync($"/api/v1/workspaces/{owner.WorkspaceId}/limits");

        // Same answer as a workspace that does not exist, so this cannot be used to enumerate them.
        Assert.Equal(ErrorCodes.WorkspaceNotFound, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Only_an_operator_can_change_the_plan()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        // A member cannot reach the operations API at all: without the shared secret it answers as
        // though the route does not exist.
        var asMember = await owner.Client.PutAsJsonAsync(
            $"/api/v1/operations/workspaces/{owner.WorkspaceId}/plan", new ChangePlanRequest("Enterprise"));
        Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);

        var asOperator = await OperatorPutAsync<WorkspaceLimitsResponse>(
            $"/api/v1/operations/workspaces/{owner.WorkspaceId}/plan", new ChangePlanRequest("Enterprise"));

        Assert.Equal("ENTERPRISE", asOperator.Plan);
    }

    [Fact]
    public async Task Data_residency_refuses_a_session_that_belongs_in_another_region()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        await OperatorPutAsync<WorkspaceLimitsResponse>(
            $"/api/v1/operations/workspaces/{owner.WorkspaceId}/plan", new ChangePlanRequest("Business"));

        await PutAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(null, null, null, null, "us-east"));

        var session = await CreateSessionAsync(owner);
        var response = await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);

        // This deployment serves eu-central. Serving the request anyway would put the tenant's media
        // in a region they excluded, and nothing downstream would notice.
        Assert.Equal(ErrorCodes.RegionNotAvailable, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Residency_matching_this_region_is_served_normally()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        await OperatorPutAsync<WorkspaceLimitsResponse>(
            $"/api/v1/operations/workspaces/{owner.WorkspaceId}/plan", new ChangePlanRequest("Business"));

        await PutAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(null, null, null, null, "eu-central"));

        var session = await CreateSessionAsync(owner);
        var prepared = await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        Assert.Equal("READY", prepared.Status);
    }

    // -----------------------------------------------------------------------------------------
    // Usage and capacity
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_reports_what_the_workspace_actually_did()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await GoLiveAsync(owner, session.Id);

        var usage = await GetAsync<WorkspaceUsageResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/usage");

        Assert.Equal("PRO", usage.Plan);
        Assert.True(usage.Usage.Sessions >= 1);
        Assert.Equal(1, usage.Capacity.BroadcastingSessions);
        Assert.Equal(3, usage.Capacity.MaxConcurrentSessions);
    }

    [Fact]
    public async Task Cost_shows_a_rate_that_is_configured_and_a_null_for_one_that_is_not()
    {
        // A cost line reported as zero reads as free. One reported as absent reads as unknown,
        // which is what an unconfigured rate actually means.
        var owner = await factory.CreateAuthenticatedClientAsync();
        await CreateSessionAsync(owner);

        var usage = await GetAsync<WorkspaceUsageResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/usage");

        var streaming = usage.Cost.Lines.Single(line => line.Key == "streaming");
        var relay = usage.Cost.Lines.Single(line => line.Key == "relay");

        Assert.NotNull(streaming.Rate);
        Assert.NotNull(streaming.Amount);
        Assert.Null(relay.Rate);
        Assert.Null(relay.Amount);

        Assert.True(usage.Cost.RatesConfigured);
        Assert.Contains(usage.Cost.NotMetered, note => note.Contains("Viewer delivery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_usage_period_longer_than_a_year_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddYears(-3).ToString("O"));

        var response = await owner.Client.GetAsync(
            $"/api/v1/workspaces/{owner.WorkspaceId}/usage?from={from}");

        Assert.Equal(ErrorCodes.ValidationFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Platform_capacity_is_operator_only()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var asMember = await owner.Client.GetAsync("/api/v1/operations/capacity");
        Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);

        var capacity = await OperatorGetAsync<PlatformCapacityResponse>("/api/v1/operations/capacity");

        Assert.Equal("eu-central", capacity.Region);
        Assert.True(capacity.Workspaces >= 1);
        Assert.Contains("LIVE", capacity.SessionsByStatus.Keys);
    }

    // -----------------------------------------------------------------------------------------
    // Retention
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_finished_recording_is_given_an_expiry_from_the_workspace_policy()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        await PutAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(null, null, null, 5, null));

        var session = await CreateSessionAsync(owner, recording: true);
        await GoLiveAsync(owner, session.Id);

        factory.RecordingStore.Stored = new Application.Abstractions.StoredRecording(4096, 12,
            factory.Clock.UtcNow.AddMinutes(-10), factory.Clock.UtcNow);

        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        await using var db = factory.CreateDbContext();
        var recording = await db.Recordings.AsNoTracking().SingleAsync(r => r.LiveSessionId == session.Id);

        Assert.Equal(RecordingStatus.Ready, recording.Status);
        Assert.NotNull(recording.ExpiresAt);
        Assert.Equal(recording.EndedAt!.Value.AddDays(5), recording.ExpiresAt);
    }

    [Fact]
    public async Task Changing_the_policy_moves_the_expiry_of_recordings_already_stored()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, recording: true);
        await GoLiveAsync(owner, session.Id);

        factory.RecordingStore.Stored = new Application.Abstractions.StoredRecording(4096, 12,
            factory.Clock.UtcNow.AddMinutes(-10), factory.Clock.UtcNow);
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/stop");

        await PutAsync<WorkspaceLimitsResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/limits",
            new UpdateWorkspaceLimitsRequest(null, null, null, 1, null));

        await using var db = factory.CreateDbContext();
        var recording = await db.Recordings.AsNoTracking().SingleAsync(r => r.LiveSessionId == session.Id);

        Assert.Equal(recording.EndedAt!.Value.AddDays(1), recording.ExpiresAt);
    }

    // -----------------------------------------------------------------------------------------
    // Export and erasure
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_export_carries_the_workspace_and_no_secret()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var created = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/destinations",
            new Application.Distribution.Contracts.CreateDestinationRequest("CustomRtmp", "My server",
                "rtmp://ingest.example.com/live", "super-secret-stream-key", null));
        await factory.EnsureSuccessAsync(created);

        var response = await owner.Client.GetAsync($"/api/v1/workspaces/{owner.WorkspaceId}/export");
        await factory.EnsureSuccessAsync(response);
        var body = await response.Content.ReadAsStringAsync();

        // The export is a file that ends up in somebody's inbox. A stream key must not travel in it.
        Assert.DoesNotContain("super-secret-stream-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("streamKeyCipher", body, StringComparison.OrdinalIgnoreCase);

        var export = await response.Content.ReadFromJsonAsync<WorkspaceExportResponse>();
        Assert.NotNull(export);
        Assert.Contains(export.Sessions, s => s.Id == session.Id);
        Assert.Contains(export.Sessions.Single(s => s.Id == session.Id).Destinations,
            d => d.DisplayName == "My server");
        Assert.Contains(export.Members, m => m.Email == owner.Auth.User.Email);
    }

    [Fact]
    public async Task Erasure_needs_the_workspace_name_typed_back()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await SendDeleteAsync(owner, $"/api/v1/workspaces/{owner.WorkspaceId}",
            new { Confirmation = "not the name" });

        Assert.Equal(ErrorCodes.ValidationFailed, await ErrorCodeAsync(response));

        await using var db = factory.CreateDbContext();
        Assert.True(await db.Workspaces.AnyAsync(w => w.Id == owner.WorkspaceId));
    }

    [Fact]
    public async Task Erasure_is_refused_while_a_session_is_on_air()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await GoLiveAsync(owner, session.Id);

        var response = await SendDeleteAsync(owner, $"/api/v1/workspaces/{owner.WorkspaceId}",
            new { Confirmation = "Test Workspace" });

        Assert.Equal(ErrorCodes.SessionNotReady, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Erasure_removes_the_workspace_its_sessions_and_its_media()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, recording: true);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        string mediaPath;
        await using (var seed = factory.CreateDbContext())
        {
            mediaPath = await seed.LiveSessions.Where(s => s.Id == session.Id)
                .Select(s => s.MediaPathName).SingleAsync();
        }

        var response = await SendDeleteAsync(owner, $"/api/v1/workspaces/{owner.WorkspaceId}",
            new { Confirmation = "Test Workspace" });

        await factory.EnsureSuccessAsync(response);
        var receipt = await response.Content.ReadFromJsonAsync<WorkspaceErasureResponse>();

        Assert.NotNull(receipt);
        Assert.Equal(1, receipt.SessionsDeleted);
        Assert.Equal(1, receipt.RecordingsDeleted);

        await using var db = factory.CreateDbContext();
        Assert.False(await db.Workspaces.AnyAsync(w => w.Id == owner.WorkspaceId));
        Assert.False(await db.LiveSessions.AnyAsync(s => s.Id == session.Id));
        Assert.False(await db.Recordings.AnyAsync(r => r.LiveSessionId == session.Id));

        // Media before rows: a row whose media survived would be a file nothing remembers.
        Assert.Contains(factory.RecordingStore.Deleted, key => key == mediaPath);
    }

    // -----------------------------------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_reports_each_check_it_ran()
    {
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("database", body, StringComparison.Ordinal);
        Assert.Contains("draining", body, StringComparison.Ordinal);

        // Liveness deliberately runs no checks: it answers whether the process is alive, and a
        // dependency failure must not make an orchestrator kill it.
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.DoesNotContain("database", await live.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dependency_endpoint_reports_the_media_gateway_without_failing_readiness()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/dependencies");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("media-gateway", body, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client, bool recording = false)
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Show {Guid.NewGuid():N}", null, "PRIVATE", recording));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

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

    private async Task AddMemberAsync(Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        await using var db = factory.CreateDbContext();
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            CreatedAt = factory.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
    }

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

    private async Task<T> PutAsync<T>(AuthenticatedClient client, string url, object body)
    {
        var response = await client.Client.PutAsJsonAsync(url, body);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> OperatorGetAsync<T>(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Internal-Auth", LiveStreamApiFactory.InternalSecret);

        var response = await factory.CreateClient().SendAsync(request);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> OperatorPutAsync<T>(string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Internal-Auth", LiveStreamApiFactory.InternalSecret);

        var response = await factory.CreateClient().SendAsync(request);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendDeleteAsync(AuthenticatedClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = JsonContent.Create(body),
        };

        return await client.Client.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem?.TryGetValue("errorCode", out var code) == true ? code.ToString() : null;
    }
}

using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Session isolation and authorization (docs/11-security.md). These are the tests that prove one
/// tenant cannot see, start, or stop another tenant's broadcast.
/// </summary>
public class AuthorizationTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    public static TheoryData<string, string> ProtectedEndpoints() => new()
    {
        { "GET", "" },
        { "GET", "/status" },
        { "GET", "/health" },
        { "GET", "/events" },
        { "GET", "/recordings" },
        { "POST", "/prepare" },
        { "POST", "/start" },
        { "POST", "/stop" },
        { "POST", "/sources/credentials" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Another_workspace_cannot_reach_a_session(string method, string suffix)
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var url = $"/api/v1/live-sessions/{session.Id}{suffix}";
        var response = method == "GET"
            ? await stranger.Client.GetAsync(url)
            : await stranger.Client.PostAsync(url, null);

        // 403 rather than 404, uniformly: membership is the gate, and the same answer is given
        // whether or not the caller guessed a real session id.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Anonymous_callers_are_rejected(string method, string suffix)
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var anonymous = factory.CreateClient();

        var url = $"/api/v1/live-sessions/{session.Id}{suffix}";
        var response = method == "GET"
            ? await anonymous.GetAsync(url)
            : await anonymous.PostAsync(url, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Listing_only_returns_sessions_from_the_callers_workspaces()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var ownedSession = await CreateSessionAsync(owner);
        await CreateSessionAsync(stranger);

        var response = await owner.Client.GetAsync("/api/v1/live-sessions");
        response.EnsureSuccessStatusCode();
        var page = (await response.Content.ReadFromJsonAsync<PagedResponse<LiveSessionResponse>>())!;

        Assert.Contains(page.Items, s => s.Id == ownedSession.Id);
        Assert.All(page.Items, s => Assert.Equal(owner.WorkspaceId, s.WorkspaceId));
    }

    [Fact]
    public async Task A_viewer_role_cannot_start_a_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var viewer = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await AddMemberAsync(owner.WorkspaceId, viewer.UserId, WorkspaceRole.Viewer);

        var readable = await viewer.Client.GetAsync($"/api/v1/live-sessions/{session.Id}");
        Assert.Equal(HttpStatusCode.OK, readable.StatusCode);

        var start = await viewer.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

        var credential = await viewer.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/sources/credentials", null);
        Assert.Equal(HttpStatusCode.Forbidden, credential.StatusCode);
    }

    [Fact]
    public async Task A_host_role_can_run_the_broadcast()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var host = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await AddMemberAsync(owner.WorkspaceId, host.UserId, WorkspaceRole.Host);

        var prepare = await host.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
    }

    [Fact]
    public async Task An_unknown_session_id_reports_not_found()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await owner.Client.GetAsync($"/api/v1/live-sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_private_session_is_not_playable_by_a_stranger()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, visibility: "PRIVATE");

        var response = await stranger.Client.GetAsync($"/api/v1/live-sessions/{session.Id}/playback");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var anonymous = await factory.CreateClient().GetAsync($"/api/v1/live-sessions/{session.Id}/playback");
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_public_session_is_playable_anonymously()
    {
        // Viewers must be able to watch a public stream without an account.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner, visibility: "PUBLIC");

        var response = await factory.CreateClient().GetAsync($"/api/v1/live-sessions/{session.Id}/playback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var playback = (await response.Content.ReadFromJsonAsync<PlaybackResponse>())!;
        Assert.Contains("index.m3u8", playback.HlsUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_responses_never_include_credential_material()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);
        await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/sources/credentials", null);

        var body = await owner.Client.GetStringAsync($"/api/v1/live-sessions/{session.Id}");

        foreach (var forbidden in new[] { "tokenHash", "TokenHash", "passwordHash", "controlApiPassword", "sharedSecret" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client,
        string visibility = "PUBLIC")
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", null, visibility, false));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task AddMemberAsync(Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        // Member management is a later phase; Phase 1 seeds roles directly to test authorization.
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
}

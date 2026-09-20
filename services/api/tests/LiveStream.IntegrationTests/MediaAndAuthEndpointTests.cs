using System.Net;
using System.Net.Http.Json;
using LiveStream.Api.Endpoints;
using LiveStream.Application.Auth;
using LiveStream.Application.Sessions.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The media gateway's auth callback: the boundary that keeps streaming credentials server-side
/// while letting the gateway admit publishers (docs/11-security.md).
/// </summary>
public class MediaCallbackTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task A_valid_token_authorizes_publishing_to_its_own_path()
    {
        var (client, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            token,
            protocol = "webrtc",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = client;
    }

    [Fact]
    public async Task A_token_cannot_publish_to_a_different_session()
    {
        // Path binding is what stops a credential for one session hijacking another.
        var (_, firstSessionId, token) = await PreparedSessionAsync();
        var (_, secondSessionId, _) = await PreparedSessionAsync();

        Assert.NotEqual(firstSessionId, secondSessionId);
        var otherPath = await MediaPathAsync(secondSessionId);

        var response = await PostCallbackAsync(new { action = "publish", path = otherPath, token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publishing_without_a_token_is_denied()
    {
        var (_, sessionId, _) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new { action = "publish", path = mediaPath });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_denied()
    {
        var (_, sessionId, _) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            token = "not-a-real-token-value",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_denied()
    {
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        try
        {
            var response = await PostCallbackAsync(new { action = "publish", path = mediaPath, token });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(-10));
        }
    }

    [Fact]
    public async Task A_revoked_token_is_denied_after_the_session_stops()
    {
        var (client, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        factory.Media.ConnectPublisher(mediaPath);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/start", null);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/stop", null);

        var response = await PostCallbackAsync(new { action = "publish", path = mediaPath, token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_callback_without_the_internal_secret_is_denied()
    {
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/internal/media/auth",
            new { action = "publish", path = mediaPath, token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_callback_with_a_wrong_internal_secret_is_denied()
    {
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MediaCallbackEndpoints.SecretHeaderName, "wrong-secret");
        var response = await client.PostAsJsonAsync("/internal/media/auth",
            new { action = "publish", path = mediaPath, token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_supplied_as_a_password_is_accepted()
    {
        // Gateways carry credentials differently per transport, so the callback accepts the token
        // in the password field too.
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            user = "broadcaster",
            password = token,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_token_supplied_in_the_query_string_is_accepted()
    {
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            query = $"token={Uri.EscapeDataString(token)}&other=1",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_public_path_is_allowed_without_a_token()
    {
        var (_, sessionId, _) = await PreparedSessionAsync(visibility: "PUBLIC");
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new { action = "read", path = mediaPath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_private_path_is_denied()
    {
        var (_, sessionId, _) = await PreparedSessionAsync(visibility: "PRIVATE");
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new { action = "read", path = mediaPath });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_gateway_actions_are_denied()
    {
        var (_, sessionId, token) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var response = await PostCallbackAsync(new { action = "api", path = mediaPath, token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_media_events_are_handled_idempotently()
    {
        var (_, sessionId, _) = await PreparedSessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MediaCallbackEndpoints.SecretHeaderName, LiveStreamApiFactory.InternalSecret);

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/internal/media/events",
                new { @event = "ready", path = mediaPath });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    [Fact]
    public async Task Media_events_for_an_unknown_path_are_ignored()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MediaCallbackEndpoints.SecretHeaderName, LiveStreamApiFactory.InternalSecret);

        var response = await client.PostAsJsonAsync("/internal/media/events",
            new { @event = "ready", path = "ls_does_not_exist" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostCallbackAsync(object payload)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MediaCallbackEndpoints.SecretHeaderName, LiveStreamApiFactory.InternalSecret);
        return await client.PostAsJsonAsync("/internal/media/auth", payload);
    }

    private async Task<(AuthenticatedClient Client, Guid SessionId, string Token)> PreparedSessionAsync(
        string visibility = "PUBLIC")
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var createResponse = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", null, visibility, false));
        createResponse.EnsureSuccessStatusCode();
        var session = (await createResponse.Content.ReadFromJsonAsync<LiveSessionResponse>())!;

        var prepare = await client.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);
        prepare.EnsureSuccessStatusCode();

        var credentialResponse = await client.Client.PostAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/credentials", null);
        credentialResponse.EnsureSuccessStatusCode();
        var credential = (await credentialResponse.Content.ReadFromJsonAsync<IngestCredentialResponse>())!;

        return (client, session.Id, credential.Token);
    }

    private async Task<string> MediaPathAsync(Guid sessionId)
    {
        await using var db = factory.CreateDbContext();
        return await db.LiveSessions.Where(s => s.Id == sessionId).Select(s => s.MediaPathName).SingleAsync();
    }
}

public class AuthEndpointTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task Registration_creates_a_workspace_owned_by_the_new_user()
    {
        var client = factory.CreateClient();
        var email = $"owner-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "New Owner", "My Studio"));

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;

        Assert.NotEmpty(auth.AccessToken);
        Assert.NotEmpty(auth.RefreshToken);
        Assert.Equal(email, auth.User.Email);

        var workspace = Assert.Single(auth.User.Workspaces);
        Assert.Equal("My Studio", workspace.Name);
        Assert.Equal("OWNER", workspace.Role);
        Assert.True(workspace.IsOwner);
    }

    [Fact]
    public async Task Registering_the_same_email_twice_is_rejected()
    {
        var client = factory.CreateClient();
        var email = $"dupe-{Guid.NewGuid():N}@example.com";
        var request = new RegisterRequest(email, "a-sufficiently-long-password", "First", null);

        (await client.PostAsJsonAsync("/api/v1/auth/register", request)).EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync("/api/v1/auth/register", request);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "a-sufficiently-long-password")]
    [InlineData("valid@example.com", "short")]
    public async Task Invalid_registration_input_is_rejected(string email, string password)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, "Name", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_credentials()
    {
        var client = factory.CreateClient();
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "Login User", null));

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "a-sufficiently-long-password"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_rejected()
    {
        var client = factory.CreateClient();
        var email = $"wrong-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "User", null));

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "definitely-the-wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_for_an_unknown_account_gives_the_same_error_as_a_wrong_password()
    {
        // Identical responses, so the endpoint cannot be used to enumerate registered emails.
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest($"ghost-{Guid.NewGuid():N}@example.com", "a-sufficiently-long-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_invalidates_the_old_one()
    {
        var client = factory.CreateClient();
        var email = $"refresh-{Guid.NewGuid():N}@example.com";
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "User", null));
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        refreshed.EnsureSuccessStatusCode();
        var rotated = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(auth.RefreshToken, rotated.RefreshToken);

        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Replaying_a_rotated_token_revokes_the_whole_chain()
    {
        var client = factory.CreateClient();
        var email = $"replay-{Guid.NewGuid():N}@example.com";
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "User", null));
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        var rotated = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;

        // Presenting the superseded token is treated as theft: the successor dies too.
        await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));

        var afterBreach = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterBreach.StatusCode);
    }

    [Fact]
    public async Task Me_requires_authentication_and_returns_the_caller()
    {
        var anonymous = await factory.CreateClient().GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var authenticated = await factory.CreateAuthenticatedClientAsync();
        var response = await authenticated.Client.GetAsync("/api/v1/auth/me");

        response.EnsureSuccessStatusCode();
        var user = (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal(authenticated.UserId, user.Id);
    }

    [Fact]
    public async Task Auth_responses_never_contain_the_password_hash()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"safe-{Guid.NewGuid():N}@example.com", "a-sufficiently-long-password", "User", null));

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", body, StringComparison.Ordinal); // PBKDF2 hash prefix.
    }
}

/// <summary>
/// The internal callback secret can also travel as a query parameter, because media gateways
/// configure one callback URL and cannot attach custom headers.
/// </summary>
public class MediaCallbackSecretTransportTests(LiveStreamApiFactory factory)
    : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task The_secret_is_accepted_as_a_query_parameter()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/internal/media/auth?{MediaCallbackEndpoints.SecretQueryName}={LiveStreamApiFactory.InternalSecret}",
            new { action = "publish", path = "ls_unknown", token = "irrelevant" });

        // Unauthorized because the token is unknown — not because the callback itself was rejected.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("errorCode", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_query_secret_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/internal/media/auth?{MediaCallbackEndpoints.SecretQueryName}=not-the-secret",
            new { action = "publish", path = "ls_unknown", token = "irrelevant" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Media_events_also_accept_the_query_secret()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/internal/media/events?{MediaCallbackEndpoints.SecretQueryName}={LiveStreamApiFactory.InternalSecret}",
            new { @event = "ready", path = "ls_unknown" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

/// <summary>
/// ICE configuration is served by the API, not compiled into the browser bundle, so infrastructure
/// addresses and any TURN credential stay under server control (ADR 0001).
/// </summary>
public class IngestCredentialIceTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task A_credential_carries_the_configured_ice_servers()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var created = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"ICE {Guid.NewGuid():N}", null, "PUBLIC", false));
        var session = (await created.Content.ReadFromJsonAsync<LiveSessionResponse>())!;

        await owner.Client.PostAsync($"/api/v1/live-sessions/{session.Id}/prepare", null);

        var response = await owner.Client.PostAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/credentials", null);
        response.EnsureSuccessStatusCode();

        var credential = (await response.Content.ReadFromJsonAsync<IngestCredentialResponse>())!;

        var stun = Assert.Single(credential.IceServers);
        Assert.Contains("stun:stun.test:3478", stun.Urls);
        Assert.Null(stun.Username);
    }

    [Fact]
    public async Task Ice_configuration_is_not_exposed_on_unauthenticated_endpoints()
    {
        // A TURN credential is a secret; it may only ride along with an authorized credential issue.
        var owner = await factory.CreateAuthenticatedClientAsync();

        var created = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"ICE {Guid.NewGuid():N}", null, "PUBLIC", false));
        var session = (await created.Content.ReadFromJsonAsync<LiveSessionResponse>())!;

        var playback = await factory.CreateClient().GetStringAsync(
            $"/api/v1/live-sessions/{session.Id}/playback");

        Assert.DoesNotContain("iceServers", playback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn:", playback, StringComparison.OrdinalIgnoreCase);
    }
}

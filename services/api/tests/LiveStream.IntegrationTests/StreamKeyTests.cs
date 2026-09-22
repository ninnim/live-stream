using System.Net;
using System.Net.Http.Json;
using LiveStream.Api.Endpoints;
using LiveStream.Application.Sessions.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// External encoder ingest: the path that lets somebody broadcast a phone game, a console through a
/// capture card, or an OBS scene (docs/decisions/0022-external-encoder-ingest.md).
///
/// The interesting assertions here are all about the credential, not the plumbing. A stream key
/// lives far longer than a browser credential because it is typed in by hand, and everything that
/// makes that acceptable — path binding, rotation, revocation, dying with the session — has to
/// hold, or the platform has quietly grown the permanent stream key it set out not to have.
/// </summary>
public class StreamKeyTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    [Fact]
    public async Task A_stream_key_is_issued_in_both_shapes_an_encoder_might_ask_for()
    {
        var (client, sessionId) = await SessionAsync();
        var key = await IssueKeyAsync(client, sessionId);

        // OBS and most phone encoders take a server and a key as two fields and join them with a
        // slash; FFmpeg takes one URL. Getting that join wrong is the commonest way an encoder
        // setup fails, so the API hands over a pre-joined URL as well as the halves.
        Assert.Equal($"{key.ServerUrl}/{key.StreamKey}", key.FullUrl);
        Assert.StartsWith("rtmp://", key.ServerUrl, StringComparison.Ordinal);
        Assert.Contains("pass=", key.StreamKey, StringComparison.Ordinal);
        Assert.NotNull(key.SrtUrl);
    }

    [Fact]
    public async Task The_key_begins_with_the_session_path_so_a_joined_url_addresses_the_right_session()
    {
        var (client, sessionId) = await SessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);

        var key = await IssueKeyAsync(client, sessionId);

        Assert.StartsWith(mediaPath, key.StreamKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_can_be_obtained_before_the_session_is_prepared()
    {
        // It has to be: it is typed into an encoder before anything goes live. The gate that the
        // session must actually be expecting ingest moves to the publish attempt, below.
        var (client, sessionId) = await SessionAsync();

        var response = await IssueKeyResponseAsync(client, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_encoder_key_authorizes_publishing_the_way_a_gateway_presents_it()
    {
        var (client, sessionId) = await SessionAsync();
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/prepare", null);

        var mediaPath = await MediaPathAsync(sessionId);
        var token = TokenFrom(await IssueKeyAsync(client, sessionId));

        // RTMP carries the credentials in the URL query, which the gateway forwards as user and
        // password. This is the exact shape the callback sees from a real encoder.
        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            user = "broadcaster",
            password = token,
            protocol = "rtmp",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Publishing_is_refused_until_the_session_expects_ingest()
    {
        // The key exists from the moment it is asked for; it opens nothing until the session has
        // been prepared. Without this, an encoder left running could re-open a path the operator
        // believed was closed.
        var (client, sessionId) = await SessionAsync();
        var mediaPath = await MediaPathAsync(sessionId);
        var token = TokenFrom(await IssueKeyAsync(client, sessionId));

        var response = await PostCallbackAsync(new
        {
            action = "publish",
            path = mediaPath,
            password = token,
            protocol = "rtmp",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotating_a_key_kills_the_previous_one_immediately()
    {
        var (client, sessionId) = await SessionAsync();
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/prepare", null);
        var mediaPath = await MediaPathAsync(sessionId);

        var first = TokenFrom(await IssueKeyAsync(client, sessionId));
        var second = TokenFrom(await IssueKeyAsync(client, sessionId));

        Assert.NotEqual(first, second);

        // This is the whole recovery for a key pasted into the wrong window: asking for it again
        // is what makes the old one useless, with no support ticket in between.
        var oldKey = await PostCallbackAsync(new { action = "publish", path = mediaPath, password = first });
        var newKey = await PostCallbackAsync(new { action = "publish", path = mediaPath, password = second });

        Assert.Equal(HttpStatusCode.Unauthorized, oldKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newKey.StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_without_touching_browser_credentials()
    {
        var (client, sessionId) = await SessionAsync();
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/prepare", null);
        var mediaPath = await MediaPathAsync(sessionId);

        var browserCredential = await client.Client.PostAsync(
            $"/api/v1/live-sessions/{sessionId}/sources/credentials", null);
        var browserToken = (await browserCredential.Content.ReadFromJsonAsync<IngestCredentialResponse>())!.Token;

        var encoderToken = TokenFrom(await IssueKeyAsync(client, sessionId));

        var revoke = await client.Client.DeleteAsync($"/api/v1/live-sessions/{sessionId}/sources/stream-key");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // "Stop that encoder" and "cut off all access" are different intentions. An operator
        // reaching for the first, mid-broadcast from the studio, must not get the second.
        var encoder = await PostCallbackAsync(new { action = "publish", path = mediaPath, password = encoderToken });
        var browser = await PostCallbackAsync(new { action = "publish", path = mediaPath, token = browserToken });

        Assert.Equal(HttpStatusCode.Unauthorized, encoder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, browser.StatusCode);
    }

    [Fact]
    public async Task A_key_cannot_publish_into_another_session()
    {
        var (mineClient, mine) = await SessionAsync();
        var (otherClient, theirs) = await SessionAsync();
        await otherClient.Client.PostAsync($"/api/v1/live-sessions/{theirs}/prepare", null);

        var token = TokenFrom(await IssueKeyAsync(mineClient, mine));
        var theirPath = await MediaPathAsync(theirs);

        var response = await PostCallbackAsync(new { action = "publish", path = theirPath, password = token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_dies_with_the_session_whatever_its_lifetime_said()
    {
        var (client, sessionId) = await SessionAsync();
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/prepare", null);
        var mediaPath = await MediaPathAsync(sessionId);
        var token = TokenFrom(await IssueKeyAsync(client, sessionId));

        factory.Media.ConnectPublisher(mediaPath);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/start", null);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/stop", null);

        // A twelve-hour key that outlives its session would be a permanent stream key by another
        // name — which is the thing this platform's credential model exists to avoid.
        var response = await PostCallbackAsync(new { action = "publish", path = mediaPath, password = token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_finished_session_will_not_issue_one_at_all()
    {
        var (client, sessionId) = await SessionAsync();
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/prepare", null);

        var mediaPath = await MediaPathAsync(sessionId);
        factory.Media.ConnectPublisher(mediaPath);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/start", null);
        await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/stop", null);

        var response = await client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/sources/stream-key", null);

        // Handing out a key that could never publish anything would only suggest otherwise.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Another_workspace_cannot_ask_for_a_key()
    {
        var (_, sessionId) = await SessionAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var response = await stranger.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/sources/stream-key", null);

        // Forbidden rather than NotFound, matching how every other session-scoped write answers a
        // stranger (AuthorizationTests). Consistency matters more here than hiding existence, which
        // the rest of the API has already decided not to do.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_deployment_with_external_ingest_off_refuses_to_issue_one()
    {
        var (client, sessionId) = await SessionAsync();

        factory.Media.ExternalIngestEnabled = false;
        try
        {
            var response = await IssueKeyResponseAsync(client, sessionId);

            // Off is the default for a deployment that does not want an open ingest port. It must
            // fail here rather than hand over a key addressed at a port nothing is listening on.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            factory.Media.ExternalIngestEnabled = true;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>The token as the encoder presents it, pulled back out of the assembled key.</summary>
    private static string TokenFrom(StreamKeyResponse key)
    {
        var pass = key.StreamKey.Split("pass=", 2)[1];
        return Uri.UnescapeDataString(pass);
    }

    private async Task<(AuthenticatedClient Client, Guid SessionId)> SessionAsync()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var created = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Encoder {Guid.NewGuid():N}", null, "PUBLIC", false));
        created.EnsureSuccessStatusCode();

        var session = (await created.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
        return (client, session.Id);
    }

    private static async Task<StreamKeyResponse> IssueKeyAsync(AuthenticatedClient client, Guid sessionId)
    {
        var response = await IssueKeyResponseAsync(client, sessionId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StreamKeyResponse>())!;
    }

    private static Task<HttpResponseMessage> IssueKeyResponseAsync(AuthenticatedClient client, Guid sessionId) =>
        client.Client.PostAsync($"/api/v1/live-sessions/{sessionId}/sources/stream-key", null);

    private async Task<string> MediaPathAsync(Guid sessionId)
    {
        await using var db = factory.CreateDbContext();
        return await db.LiveSessions.Where(s => s.Id == sessionId).Select(s => s.MediaPathName).SingleAsync();
    }

    private async Task<HttpResponseMessage> PostCallbackAsync(object payload)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MediaCallbackEndpoints.SecretHeaderName, LiveStreamApiFactory.InternalSecret);
        return await client.PostAsJsonAsync("/internal/media/auth", payload);
    }
}

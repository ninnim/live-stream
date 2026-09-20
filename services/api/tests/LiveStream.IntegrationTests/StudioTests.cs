using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources.Contracts;
using LiveStream.Application.Studio.Contracts;
using LiveStream.Domain.Studio;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Branding and scenes over HTTP (docs/07-live-studio.md,
/// implementation/phase-5-professional-live-studio.md).
///
/// Both are configuration rather than live state, so the tests here are about persistence,
/// validation, and the two ways this data can outlive what it points at: a scene naming a source
/// that has been revoked, and a scene naming a source from another session entirely.
/// </summary>
public class StudioTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // Branding
    // -----------------------------------------------------------------------------------------

    /// <summary>A session is never without branding, so the studio never has to handle its absence.</summary>
    [Fact]
    public async Task A_new_session_already_has_branding()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var branding = await GetAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding");

        Assert.Equal(SessionBranding.DefaultAccentColor, branding.AccentColor);
        Assert.False(branding.ShowLogo);
        Assert.Null(branding.LogoDataUri);
    }

    [Fact]
    public async Task Branding_survives_a_round_trip()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var saved = await PutAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(TinyPng, true, "BottomLeft", 55, "#ff8800", true));

        Assert.Equal(TinyPng, saved.LogoDataUri);
        Assert.Equal("BottomLeft", saved.LogoPosition);
        Assert.Equal(55, saved.LogoOpacityPercent);
        // Normalised on the way in, because this value is written into a canvas fill style.
        Assert.Equal("#FF8800", saved.AccentColor);
        Assert.True(saved.ShowLogo);

        var read = await GetAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding");
        Assert.Equal(saved.LogoDataUri, read.LogoDataUri);
    }

    /// <summary>
    /// Saving a colour change from a form that does not carry the image must not delete the image.
    /// </summary>
    [Fact]
    public async Task Updating_branding_without_replacing_the_logo_keeps_it()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await PutAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(TinyPng, true, null, null, null, true));

        var updated = await PutAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(null, false, null, null, "#123456", null));

        Assert.Equal(TinyPng, updated.LogoDataUri);
        Assert.Equal("#123456", updated.AccentColor);
        Assert.True(updated.ShowLogo);
    }

    [Fact]
    public async Task Clearing_the_logo_also_turns_the_watermark_off()
    {
        // Leaving "show the logo" set with nothing to draw would report a state the frame does not
        // have.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        await PutAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(TinyPng, true, null, null, null, true));

        var cleared = await PutAsync<BrandingResponse>(owner, $"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(null, true, null, null, null, null));

        Assert.Null(cleared.LogoDataUri);
        Assert.False(cleared.ShowLogo);
    }

    [Fact]
    public async Task A_logo_that_is_not_an_image_is_refused()
    {
        // This value is drawn into the composition canvas. Anything that is not an image is either
        // a mistake or an attempt to put something else there.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest("data:text/html;base64,PHNjcmlwdD4=", true, null, null, null, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_accent_colour_that_is_not_a_hex_value_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        foreach (var colour in new[] { "red", "#12345", "javascript:alert(1)", "#GGGGGG" })
        {
            var response = await owner.Client.PutAsJsonAsync($"/api/v1/live-sessions/{session.Id}/branding",
                new UpdateBrandingRequest(null, false, null, null, colour, null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Another_workspace_cannot_read_or_change_branding()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var read = await stranger.Client.GetAsync($"/api/v1/live-sessions/{session.Id}/branding");
        var write = await stranger.Client.PutAsJsonAsync($"/api/v1/live-sessions/{session.Id}/branding",
            new UpdateBrandingRequest(null, false, null, null, "#000000", null));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Scenes
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scene_can_be_saved_listed_updated_and_removed()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var studio = await StudioSourceAsync(owner, session.Id);

        var created = await PostAsync<SceneResponse>(owner, $"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("Opening", "Solo", studio.Id, null, "Ada Lovelace", "Analytical Engines"));

        Assert.Equal("Opening", created.Name);
        Assert.Equal("Solo", created.Layout);
        Assert.Equal(studio.Id, created.PrimarySourceId);
        Assert.Equal("Ada Lovelace", created.LowerThirdTitle);

        var listed = await GetAsync<List<SceneResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/scenes");
        Assert.Single(listed);

        var updated = await PutAsync<SceneResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/scenes/{created.Id}",
            new SaveSceneRequest("Interview", "SideBySide", studio.Id, null, "Ada Lovelace", null));

        Assert.Equal("Interview", updated.Name);
        Assert.Equal("SideBySide", updated.Layout);
        Assert.Null(updated.LowerThirdSubtitle);

        var deleted = await owner.Client.DeleteAsync($"/api/v1/live-sessions/{session.Id}/scenes/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(await GetAsync<List<SceneResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/scenes"));
    }

    [Fact]
    public async Task Scenes_come_back_in_the_order_they_were_saved()
    {
        // Scenes are chosen by position under time pressure, so the order has to be stable.
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        foreach (var name in new[] { "Opening", "Interview", "Closing" })
        {
            await PostAsync<SceneResponse>(owner, $"/api/v1/live-sessions/{session.Id}/scenes",
                new SaveSceneRequest(name, "Solo", null, null, null, null));
        }

        var scenes = await GetAsync<List<SceneResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/scenes");

        Assert.Equal(["Opening", "Interview", "Closing"], scenes.Select(s => s.Name));
    }

    [Fact]
    public async Task A_scene_cannot_show_the_same_source_twice()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var studio = await StudioSourceAsync(owner, session.Id);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("Doubled", "SideBySide", studio.Id, studio.Id, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Scenes hold source ids without a foreign key, so nothing in the database stops one session's
    /// scene naming another's source. This is the check that does.
    /// </summary>
    [Fact]
    public async Task A_scene_cannot_name_a_source_from_another_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var other = await CreateSessionAsync(owner);
        var otherStudio = await StudioSourceAsync(owner, other.Id);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("Borrowed", "Solo", otherStudio.Id, null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Revoking a camera must leave its scenes usable. A scene still pointing at a removed source
    /// would silently do nothing when recalled, which reads as the scene being broken.
    /// </summary>
    [Fact]
    public async Task Revoking_a_source_clears_it_from_every_scene()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var studio = await StudioSourceAsync(owner, session.Id);

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Guest camera");

        var scene = await PostAsync<SceneResponse>(owner, $"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("Two shot", "SideBySide", studio.Id, invitation.Source.Id, null, null));

        Assert.Equal(invitation.Source.Id, scene.SecondarySourceId);

        var revoked = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{invitation.Source.Id}");
        await factory.EnsureSuccessAsync(revoked);

        var after = await GetAsync<List<SceneResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/scenes");

        Assert.Null(after.Single().SecondarySourceId);
        // The scene itself survives, and still names the source that remains.
        Assert.Equal(studio.Id, after.Single().PrimarySourceId);
    }

    [Fact]
    public async Task A_scene_cannot_be_reached_through_a_different_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var other = await CreateSessionAsync(owner);

        var scene = await PostAsync<SceneResponse>(owner, $"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("Opening", "Solo", null, null, null, null));

        var response = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{other.Id}/scenes/{scene.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LIVE_028_SCENE_NOT_FOUND", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unnamed_scene_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("   ", "Solo", null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_scene_limit_is_enforced()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        for (var i = 0; i < SessionScene.MaxScenesPerSession; i++)
        {
            await PostAsync<SceneResponse>(owner, $"/api/v1/live-sessions/{session.Id}/scenes",
                new SaveSceneRequest($"Scene {i}", "Solo", null, null, null, null));
        }

        var response = await owner.Client.PostAsJsonAsync($"/api/v1/live-sessions/{session.Id}/scenes",
            new SaveSceneRequest("One too many", "Solo", null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_read_or_change_the_studio()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/live-sessions/{session.Id}/scenes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/live-sessions/{session.Id}/branding")).StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>A 1×1 transparent PNG — the smallest thing that is genuinely an image.</summary>
    private const string TinyPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client)
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", "Studio test", "PUBLIC", false));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task<SourceResponse> StudioSourceAsync(AuthenticatedClient client, Guid sessionId)
    {
        var sources = await GetAsync<List<SourceResponse>>(client, $"/api/v1/live-sessions/{sessionId}/sources");
        return sources.Single(s => s.Role == "Host");
    }

    private async Task<SourceInvitationResponse> InviteAsync(AuthenticatedClient client, Guid sessionId,
        string role, string name)
    {
        var response = await client.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{sessionId}/sources/invitations", new InviteSourceRequest(role, name));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<SourceInvitationResponse>())!;
    }

    private async Task<T> GetAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.GetAsync(url);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(AuthenticatedClient client, string url, object body)
    {
        var response = await client.Client.PostAsJsonAsync(url, body);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PutAsync<T>(AuthenticatedClient client, string url, object body)
    {
        var response = await client.Client.PutAsJsonAsync(url, body);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem?.TryGetValue("errorCode", out var code) == true ? code.ToString() : null;
    }
}

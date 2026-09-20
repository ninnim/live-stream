using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources;
using LiveStream.Application.Sources.Contracts;
using LiveStream.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The Phase 4 acceptance criterion, over HTTP against the real API:
/// <i>"Multiple authorized devices can join one session and expose their source/status to the
/// control room without sharing permanent credentials."</i>
///
/// Only the media plane is substituted. Pairing, device authentication, role permissions, presence
/// reconciliation and revocation all run for real.
/// </summary>
public class SourceTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // The journey
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_device_joins_with_a_code_publishes_and_appears_in_the_control_room()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        // 1. The operator invites a device. The code comes back exactly once.
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        Assert.NotEmpty(invitation.PairingCode);
        Assert.Equal("INVITED", invitation.Source.Status.ToUpperInvariant());
        Assert.Contains("/join/", invitation.JoinUrl, StringComparison.Ordinal);

        // 2. The device redeems it, with no account of its own.
        var paired = await ClaimAsync(invitation.PairingCode, "Alice's iPhone");

        Assert.NotEmpty(paired.DeviceToken);
        Assert.Equal(session.Id, paired.LiveSessionId);
        Assert.Equal("PAIRED", paired.Source.Status.ToUpperInvariant());

        // The operator named this device; joining does not rename it.
        Assert.Equal("Second camera", paired.Source.DisplayName);

        // 3. It can now see the session it joined, and only what its role permits.
        var device = DeviceClient(paired.DeviceToken);
        var view = await GetAsync<DeviceSessionResponse>(device, "/api/v1/device/session");

        Assert.Equal(session.Id, view.LiveSessionId);
        Assert.Contains("PublishMedia", view.Permissions);
        Assert.DoesNotContain("ManageDevices", view.Permissions);

        // 4. It gets a credential for its own ingest path — not the studio's.
        var credential = await PostAsync<IngestCredentialResponse>(device, "/api/v1/device/credentials");

        Assert.NotEmpty(credential.Token);
        Assert.DoesNotContain(await MediaPathAsync(session.Id), credential.IngestUrl, StringComparison.Ordinal);

        // 5. The device publishes, and the control room sees it without the device saying so.
        var sourcePath = await SourceMediaPathAsync(paired.Source.Id);
        factory.Media.ConnectPublisher(sourcePath, bytesReceived: 500_000);
        await ReconcileSourcesAsync();

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var camera = sources.Single(s => s.Id == paired.Source.Id);

        Assert.Equal("CONNECTED", camera.Status.ToUpperInvariant());
        Assert.True(camera.IngestConnected);
        Assert.NotNull(camera.LastSeenAt);
    }

    /// <summary>Every session has a studio source from the outset, so the list is never empty.</summary>
    [Fact]
    public async Task A_new_session_already_has_its_studio_source()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var host = Assert.Single(sources);

        Assert.Equal("HOST", host.Role.ToUpperInvariant());
        Assert.Equal("PAIRED", host.Status.ToUpperInvariant());
        Assert.True(host.IsProgram);
    }

    // -----------------------------------------------------------------------------------------
    // Credentials are never permanent, and never leak
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The pairing code appears in the invitation response and nowhere else, ever. Asserted against
    /// raw JSON rather than the typed contract, because a leak would come from a field being added.
    /// </summary>
    [Fact]
    public async Task The_pairing_code_never_appears_again_after_the_invitation()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        var code = SessionSource.NormalizeCode(invitation.PairingCode);

        string[] urls =
        [
            $"/api/v1/live-sessions/{session.Id}/sources",
            $"/api/v1/live-sessions/{session.Id}/sources/{invitation.Source.Id}/events",
            $"/api/v1/live-sessions/{session.Id}",
        ];

        foreach (var url in urls)
        {
            var body = await owner.Client.GetStringAsync(url);
            Assert.DoesNotContain(code, SessionSource.NormalizeCode(body), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Neither_secret_is_stored_in_the_clear()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.SessionSources.SingleAsync(s => s.Id == invitation.Source.Id);
            Assert.NotNull(stored.PairingCodeHash);
            Assert.Equal(64, stored.PairingCodeHash.Length);
            Assert.NotEqual(SessionSource.NormalizeCode(invitation.PairingCode), stored.PairingCodeHash);
        }

        var paired = await ClaimAsync(invitation.PairingCode, null);

        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.SessionSources.SingleAsync(s => s.Id == invitation.Source.Id);
            Assert.Null(stored.PairingCodeHash);
            Assert.NotNull(stored.DeviceTokenHash);
            Assert.NotEqual(paired.DeviceToken, stored.DeviceTokenHash);
        }
    }

    [Fact]
    public async Task A_pairing_code_can_only_be_used_once()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        await ClaimAsync(invitation.PairingCode, null);

        var second = await ClaimRawAsync(invitation.PairingCode);
        Assert.Equal("LIVE_024_PAIRING_CODE_INVALID", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task An_expired_pairing_code_is_refused()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        factory.Clock.Advance(TimeSpan.FromHours(1));

        var response = await ClaimRawAsync(invitation.PairingCode);
        Assert.Equal("LIVE_024_PAIRING_CODE_INVALID", await ErrorCodeAsync(response));
    }

    /// <summary>
    /// Unknown, expired and already-used all return the same code. Telling them apart would confirm
    /// which guesses had ever been real.
    /// </summary>
    [Fact]
    public async Task An_unknown_code_is_indistinguishable_from_a_spent_one()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        await ClaimAsync(invitation.PairingCode, null);

        var spent = await ClaimRawAsync(invitation.PairingCode);
        var unknown = await ClaimRawAsync("ZZZZ-ZZZZ");

        Assert.Equal(spent.StatusCode, unknown.StatusCode);
        Assert.Equal(await ErrorCodeAsync(spent), await ErrorCodeAsync(unknown));
    }

    [Fact]
    public async Task A_code_is_accepted_however_it_was_typed()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        // The operator reads "ABCD-EFGH" aloud and someone types it in lower case with a space.
        var typed = SessionSource.NormalizeCode(invitation.PairingCode).ToLowerInvariant();
        var spaced = $"{typed[..4]} {typed[4..]}";

        var paired = await ClaimAsync(spaced, null);
        Assert.Equal(invitation.Source.Id, paired.Source.Id);
    }

    // -----------------------------------------------------------------------------------------
    // Revocation is immediate
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The security property Phase 4 turns on. The device's very next request fails, and whatever
    /// it was publishing is severed at the gateway rather than left running until a credential
    /// happens to expire.
    /// </summary>
    [Fact]
    public async Task Revoking_a_device_stops_it_on_the_next_request_and_severs_its_media()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);
        var device = DeviceClient(paired.DeviceToken);

        // It works before revocation.
        var before = await device.GetAsync("/api/v1/device/session");
        Assert.True(before.IsSuccessStatusCode);

        var sourcePath = await SourceMediaPathAsync(paired.Source.Id);
        factory.Media.ConnectPublisher(sourcePath);

        var revoked = await DeleteAsync<SourceResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}");
        Assert.Equal("REVOKED", revoked.Status.ToUpperInvariant());

        // The device is locked out immediately — not when its token expires.
        var after = await device.GetAsync("/api/v1/device/session");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        var credentialAttempt = await device.PostAsync("/api/v1/device/credentials", null);
        Assert.Equal(HttpStatusCode.Unauthorized, credentialAttempt.StatusCode);

        // Its media connection was severed rather than left to run out.
        Assert.Contains(sourcePath, factory.Media.KickedPaths);

        // And every credential it held is dead.
        await using var db = factory.CreateDbContext();
        var credentials = await db.IngestCredentials.Where(c => c.MediaPathName == sourcePath).ToListAsync();
        Assert.All(credentials, c => Assert.NotNull(c.RevokedAt));
    }

    [Fact]
    public async Task A_revoked_device_cannot_pair_again_with_the_same_code()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        await DeleteAsync<SourceResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}");

        var response = await ClaimRawAsync(invitation.PairingCode);
        Assert.Equal("LIVE_024_PAIRING_CODE_INVALID", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task The_studio_source_cannot_be_revoked()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var host = sources.Single(s => s.Role == "Host");

        var response = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{host.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Role permissions
    // -----------------------------------------------------------------------------------------

    /// <summary>A moderator joins to moderate, not to broadcast.</summary>
    [Fact]
    public async Task A_moderator_device_cannot_obtain_a_publish_credential()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await InviteAsync(owner, session.Id, "Moderator", "Chat moderator");
        var paired = await ClaimAsync(invitation.PairingCode, null);
        var device = DeviceClient(paired.DeviceToken);

        var response = await device.PostAsync("/api/v1/device/credentials", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("LIVE_008_PERMISSION_DENIED", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_device_token_is_not_accepted_as_a_user_token()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        // Presented as a Bearer token to an operator endpoint, it must be rejected outright.
        var impostor = factory.CreateClient();
        impostor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.DeviceToken);

        var response = await impostor.GetAsync($"/api/v1/live-sessions/{session.Id}/sources");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_token_is_not_accepted_as_a_device_token()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var impostor = factory.CreateClient();
        impostor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Device", owner.Auth.AccessToken);

        var response = await impostor.GetAsync("/api/v1/device/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Tenancy and limits
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspace_cannot_see_or_manage_sources()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var stranger = await factory.CreateAuthenticatedClientAsync();

        var session = await CreateSessionAsync(owner);
        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");

        var read = await stranger.Client.GetAsync($"/api/v1/live-sessions/{session.Id}/sources");
        var invite = await stranger.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/invitations",
            new InviteSourceRequest("Camera", "Sneaky camera"));
        var revoke = await stranger.Client.DeleteAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{invitation.Source.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    /// <summary>
    /// A source id from one session must not be operable through another the caller can reach.
    /// Without the parent check this is a cross-tenant write.
    /// </summary>
    [Fact]
    public async Task A_source_cannot_be_reached_through_a_different_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var sessionA = await CreateSessionAsync(owner);
        var sessionB = await CreateSessionAsync(owner);

        var invitation = await InviteAsync(owner, sessionA.Id, "Camera", "Camera A");

        var response = await owner.Client.DeleteAsync(
            $"/api/v1/live-sessions/{sessionB.Id}/sources/{invitation.Source.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LIVE_025_SOURCE_NOT_FOUND", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task The_source_limit_is_enforced()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        // The factory configures a limit of four, and the studio source already occupies one.
        for (var i = 0; i < 3; i++)
        {
            await InviteAsync(owner, session.Id, "Camera", $"Camera {i}");
        }

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/invitations",
            new InviteSourceRequest("Camera", "One too many"));

        Assert.Equal("LIVE_027_SOURCE_LIMIT_REACHED", await ErrorCodeAsync(response));
    }

    /// <summary>A revoked source frees its seat; the audit row stays.</summary>
    [Fact]
    public async Task Revoking_a_source_frees_a_slot()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var first = await InviteAsync(owner, session.Id, "Camera", "Camera 0");
        await InviteAsync(owner, session.Id, "Camera", "Camera 1");
        await InviteAsync(owner, session.Id, "Camera", "Camera 2");

        await DeleteAsync<SourceResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{first.Source.Id}");

        // The seat is available again.
        var replacement = await InviteAsync(owner, session.Id, "Camera", "Replacement camera");
        Assert.Equal("INVITED", replacement.Source.Status.ToUpperInvariant());

        // ... and the revoked row is still there to be audited.
        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        Assert.Contains(sources, s => s.Id == first.Source.Id && s.Status == "Revoked");
    }

    [Fact]
    public async Task Anonymous_callers_cannot_manage_sources()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/v1/live-sessions/{session.Id}/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Presence
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_source_that_stops_sending_is_reported_as_disconnected()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);
        var sourcePath = await SourceMediaPathAsync(paired.Source.Id);

        factory.Media.ConnectPublisher(sourcePath, bytesReceived: 100_000);
        await ReconcileSourcesAsync();
        Assert.Equal("Connected", (await SourceAsync(owner, session.Id, paired.Source.Id)).Status);

        factory.Media.DisconnectPublisher(sourcePath);

        // The grace period keeps a brief blip from being reported as a departure...
        await ReconcileSourcesAsync();
        Assert.Equal("Connected", (await SourceAsync(owner, session.Id, paired.Source.Id)).Status);

        // ... but a real absence is.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await ReconcileSourcesAsync();
        Assert.Equal("Disconnected", (await SourceAsync(owner, session.Id, paired.Source.Id)).Status);
    }

    /// <summary>A device dropping out must never disturb the session it is contributing to.</summary>
    [Fact]
    public async Task A_source_disconnecting_does_not_affect_the_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id), bytesReceived: 2_000_000);
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);
        var sourcePath = await SourceMediaPathAsync(paired.Source.Id);

        factory.Media.ConnectPublisher(sourcePath);
        await ReconcileSourcesAsync();

        factory.Media.RemovePath(sourcePath);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await ReconcileSourcesAsync();

        Assert.Equal("Disconnected", (await SourceAsync(owner, session.Id, paired.Source.Id)).Status);

        var status = await GetAsync<LiveSessionStatusResponse>(owner,
            $"/api/v1/live-sessions/{session.Id}/status");
        Assert.Equal("LIVE", status.Status);
    }

    // -----------------------------------------------------------------------------------------
    // Program
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The Phase 5 acceptance criterion for switching: exactly one source is on air, and putting a
    /// second one there takes the first off in the same breath.
    /// </summary>
    [Fact]
    public async Task Putting_a_source_on_air_takes_the_previous_one_off()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        // The studio holds the program from the outset, so there is always something on air.
        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var studio = sources.Single(s => s.Role == "Host");
        Assert.True(studio.IsProgram);

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        factory.Media.ConnectPublisher(await SourceMediaPathAsync(paired.Source.Id), bytesReceived: 500_000);
        await ReconcileSourcesAsync();

        var changed = await PostAsync<List<SourceResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/program");

        // Both ends of the swap come back, so a control room can update without re-reading.
        Assert.Equal(2, changed.Count);
        Assert.True(changed.Single(s => s.Id == paired.Source.Id).IsProgram);
        Assert.False(changed.Single(s => s.Id == studio.Id).IsProgram);

        var after = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        Assert.Single(after, s => s.IsProgram);
        Assert.Equal(paired.Source.Id, after.Single(s => s.IsProgram).Id);
    }

    /// <summary>
    /// Cutting away from the studio and back again.
    ///
    /// The return leg is the one that can quietly break: coming back requires the studio source to
    /// be reported as connected, which nothing asserts unless the round trip is actually made.
    /// </summary>
    [Fact]
    public async Task The_program_can_be_cut_back_to_the_studio()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        factory.Media.ConnectPublisher(await MediaPathAsync(session.Id), bytesReceived: 2_000_000);
        await PostAsync<LiveSessionStatusResponse>(owner, $"/api/v1/live-sessions/{session.Id}/start");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);
        factory.Media.ConnectPublisher(await SourceMediaPathAsync(paired.Source.Id), bytesReceived: 500_000);
        await ReconcileSourcesAsync();

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var studio = sources.Single(s => s.Role == "Host");

        await PostAsync<List<SourceResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/program");

        var back = await PostAsync<List<SourceResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{studio.Id}/program");

        Assert.True(back.Single(s => s.Id == studio.Id).IsProgram);
        Assert.False(back.Single(s => s.Id == paired.Source.Id).IsProgram);
    }

    /// <summary>
    /// A source that is not sending cannot be put on air. Allowing it would cut to black, which is
    /// the one outcome a program switch must never produce.
    /// </summary>
    [Fact]
    public async Task A_source_that_is_not_sending_cannot_be_put_on_air()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        // Paired, but no media has ever arrived.
        var response = await owner.Client.PostAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/program", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // And the studio is still on air, rather than the session having been left with no program.
        var after = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        Assert.Equal("Host", after.Single(s => s.IsProgram).Role);
    }

    /// <summary>A source that sends no media at all is never a candidate for the program.</summary>
    [Fact]
    public async Task A_moderator_cannot_be_put_on_air()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var invitation = await InviteAsync(owner, session.Id, "Moderator", "Chat moderator");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        var response = await owner.Client.PostAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/program", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Re-cutting to whatever is already on air is a no-op, not an error or a flicker.</summary>
    [Fact]
    public async Task Putting_the_current_program_on_air_again_changes_nothing()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var studio = sources.Single(s => s.Role == "Host");

        var changed = await PostAsync<List<SourceResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{studio.Id}/program");

        Assert.Single(changed);
        Assert.True(changed[0].IsProgram);

        var after = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        Assert.Single(after, s => s.IsProgram);
    }

    /// <summary>Program changes are part of the session's audit trail, like every other decision.</summary>
    [Fact]
    public async Task A_program_change_is_recorded_against_both_sources()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        await PostAsync<LiveSessionResponse>(owner, $"/api/v1/live-sessions/{session.Id}/prepare");

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var studio = sources.Single(s => s.Role == "Host");

        var invitation = await InviteAsync(owner, session.Id, "Camera", "Second camera");
        var paired = await ClaimAsync(invitation.PairingCode, null);

        factory.Media.ConnectPublisher(await SourceMediaPathAsync(paired.Source.Id), bytesReceived: 500_000);
        await ReconcileSourcesAsync();

        await PostAsync<List<SourceResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/program");

        var promoted = await GetAsync<List<SourceEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{paired.Source.Id}/events");
        var demoted = await GetAsync<List<SourceEventResponse>>(owner,
            $"/api/v1/live-sessions/{session.Id}/sources/{studio.Id}/events");

        Assert.Contains(promoted, e => e.Type == "PromotedToProgram");
        Assert.Contains(demoted, e => e.Type == "RemovedFromProgram");
    }

    /// <summary>
    /// Deciding what goes on air is an operator action, and carries the same two protections as
    /// every other one: another workspace is refused, and a source id cannot be smuggled in through
    /// a session the caller does happen to own.
    /// </summary>
    [Fact]
    public async Task The_program_cannot_be_changed_from_outside_the_session()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(owner);
        var other = await CreateSessionAsync(owner);

        var sources = await GetAsync<List<SourceResponse>>(owner, $"/api/v1/live-sessions/{session.Id}/sources");
        var studio = sources.Single(s => s.Role == "Host");

        var stranger = await factory.CreateAuthenticatedClientAsync();
        var fromAnotherWorkspace = await stranger.Client.PostAsync(
            $"/api/v1/live-sessions/{session.Id}/sources/{studio.Id}/program", null);

        var throughAnotherSession = await owner.Client.PostAsync(
            $"/api/v1/live-sessions/{other.Id}/sources/{studio.Id}/program", null);

        Assert.Equal(HttpStatusCode.Forbidden, fromAnotherWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughAnotherSession.StatusCode);
        Assert.Equal("LIVE_025_SOURCE_NOT_FOUND", await ErrorCodeAsync(throughAnotherSession));
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task<LiveSessionResponse> CreateSessionAsync(AuthenticatedClient client)
    {
        var response = await client.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest($"Session {Guid.NewGuid():N}", "Multi-device test", "PUBLIC", false));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<LiveSessionResponse>())!;
    }

    private async Task<SourceInvitationResponse> InviteAsync(AuthenticatedClient client, Guid sessionId,
        string role, string name)
    {
        var response = await client.Client.PostAsJsonAsync(
            $"/api/v1/live-sessions/{sessionId}/sources/invitations", new InviteSourceRequest(role, name));

        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<SourceInvitationResponse>())!;
    }

    private async Task<DevicePairedResponse> ClaimAsync(string code, string? label)
    {
        var response = await ClaimRawAsync(code, label);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<DevicePairedResponse>())!;
    }

    private Task<HttpResponseMessage> ClaimRawAsync(string code, string? label = null) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/pairings/claim", new ClaimPairingRequest(code, label));

    /// <summary>A client authenticated as a device rather than a user.</summary>
    private HttpClient DeviceClient(string deviceToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        return client;
    }

    private async Task<SourceResponse> SourceAsync(AuthenticatedClient client, Guid sessionId, Guid sourceId)
    {
        var sources = await GetAsync<List<SourceResponse>>(client, $"/api/v1/live-sessions/{sessionId}/sources");
        return sources.Single(s => s.Id == sourceId);
    }

    /// <summary>
    /// Runs one presence pass directly. The hosted monitor is removed in tests so timing is
    /// deterministic — the same approach the session and destination suites take.
    /// </summary>
    private async Task ReconcileSourcesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<SourceReconciler>();
        await reconciler.ReconcileActiveSourcesAsync(CancellationToken.None);
    }

    private async Task<string> MediaPathAsync(Guid sessionId)
    {
        await using var db = factory.CreateDbContext();
        return await db.LiveSessions.Where(s => s.Id == sessionId).Select(s => s.MediaPathName).SingleAsync();
    }

    private async Task<string> SourceMediaPathAsync(Guid sourceId)
    {
        await using var db = factory.CreateDbContext();
        return await db.SessionSources.Where(s => s.Id == sourceId).Select(s => s.MediaPathName).SingleAsync();
    }

    private async Task<T> PostAsync<T>(AuthenticatedClient client, string url) =>
        await PostAsync<T>(client.Client, url);

    private async Task<T> PostAsync<T>(HttpClient client, string url)
    {
        var response = await client.PostAsync(url, null);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(AuthenticatedClient client, string url) => await GetAsync<T>(client.Client, url);

    private async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> DeleteAsync<T>(AuthenticatedClient client, string url)
    {
        var response = await client.Client.DeleteAsync(url);
        await factory.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem?.TryGetValue("errorCode", out var code) == true ? code.ToString() : null;
    }
}

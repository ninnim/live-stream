using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Auth;
using LiveStream.Application.Governance.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Enterprise single sign-on over HTTP (implementation/phase-7: "SSO").
///
/// The identity provider is faked, exactly as the media gateway is: it is somebody else's process.
/// Everything this platform decides around it — who may claim a domain, who may verify one, which
/// account an identity maps to, and what role it gets — is exercised for real.
///
/// The property most of these tests defend: <em>an unverified domain signs nobody in</em>. Whoever
/// holds a domain decides who may sign in with an address in it, so a self-service claim would be
/// an account-takeover primitive rather than a configuration mistake.
/// </summary>
public class SsoTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    // -----------------------------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Single_sign_on_is_not_available_below_the_business_plan()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(Domain(owner)));

        Assert.Equal(ErrorCodes.PlanLimitReached, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_saved_connection_never_returns_its_client_secret()
    {
        var owner = await BusinessWorkspaceAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(Domain(owner), clientSecret: "the-client-secret"));

        await factory.EnsureSuccessAsync(response);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("the-client-secret", body, StringComparison.Ordinal);

        var connection = await response.Content.ReadFromJsonAsync<SsoConnectionResponse>();
        Assert.NotNull(connection);
        Assert.Equal("http://studio.test/sign-in/sso/callback", connection.RedirectUri);

        // Stored encrypted, like a destination stream key — not merely omitted from the response.
        await using var db = factory.CreateDbContext();
        var stored = await db.SsoConnections.AsNoTracking()
            .SingleAsync(c => c.WorkspaceId == owner.WorkspaceId);
        Assert.DoesNotContain("the-client-secret", stored.ClientSecretCiphertext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claimed_domain_starts_unverified_and_the_connection_is_unusable()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = Domain(owner);

        var connection = await PutAsync<SsoConnectionResponse>(owner,
            $"/api/v1/workspaces/{owner.WorkspaceId}/sso", Request(domain));

        Assert.False(connection.Usable);
        Assert.False(connection.Domains.Single().Verified);

        var discovery = await DiscoverAsync($"someone@{domain}");
        Assert.False(discovery.Available);
    }

    [Fact]
    public async Task A_workspace_cannot_verify_its_own_domain()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = Domain(owner);
        await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso", Request(domain));

        // There is no member-facing route at all, and the operations one is invisible without the
        // shared secret.
        var response = await owner.Client.PutAsJsonAsync($"/api/v1/operations/sso-domains/{domain}",
            new { Verified = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_domain_already_claimed_elsewhere_cannot_be_claimed_again()
    {
        var first = await BusinessWorkspaceAsync();
        var domain = Domain(first);
        await PutAsync<SsoConnectionResponse>(first, $"/api/v1/workspaces/{first.WorkspaceId}/sso", Request(domain));

        var second = await BusinessWorkspaceAsync();
        var response = await second.Client.PutAsJsonAsync($"/api/v1/workspaces/{second.WorkspaceId}/sso",
            Request(domain));

        Assert.Equal(ErrorCodes.ValidationFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Single_sign_on_cannot_be_configured_to_grant_administrator()
    {
        // Otherwise an identity provider could mint somebody able to reconfigure the identity
        // provider, and the workspace would have handed over its own front door.
        var owner = await BusinessWorkspaceAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(Domain(owner), defaultRole: "Admin"));

        Assert.Equal(ErrorCodes.ValidationFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_issuer_that_is_not_https_is_refused()
    {
        var owner = await BusinessWorkspaceAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(Domain(owner), issuer: "http://login.example.com"));

        // Discovery, the token endpoint, and the signing keys all come from the issuer. Over plain
        // HTTP, whoever is on the path chooses who may sign in.
        Assert.Equal(ErrorCodes.ValidationFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Re_saving_a_connection_does_not_re_verify_a_domain_or_clear_the_secret()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = Domain(owner);
        await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(domain, clientSecret: "first-secret"));
        await VerifyDomainAsync(domain);

        // No secret in the body: the form that edits the display settings does not carry it, and
        // saving must not blank it.
        var updated = await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(domain, clientSecret: null, jitProvisioning: false));

        Assert.True(updated.Domains.Single().Verified);
        Assert.False(updated.JitProvisioning);

        await using var db = factory.CreateDbContext();
        var stored = await db.SsoConnections.AsNoTracking().SingleAsync(c => c.WorkspaceId == owner.WorkspaceId);
        Assert.NotEmpty(stored.ClientSecretCiphertext);
    }

    [Fact]
    public async Task Adding_a_domain_leaves_it_unverified_while_the_existing_one_stays_verified()
    {
        var owner = await BusinessWorkspaceAsync();
        var verified = Domain(owner);
        await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso", Request(verified));
        await VerifyDomainAsync(verified);

        var added = $"added-{Guid.NewGuid():N}.example.com";
        var connection = await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(verified, extraDomains: [added]));

        Assert.True(connection.Domains.Single(d => d.Domain == verified).Verified);
        Assert.False(connection.Domains.Single(d => d.Domain == added).Verified);
    }

    // -----------------------------------------------------------------------------------------
    // Sign-in
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_domain_offers_single_sign_on_and_starts_a_flow()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        var discovery = await DiscoverAsync($"someone@{domain}");
        Assert.True(discovery.Available);
        Assert.Equal("Test Workspace", discovery.WorkspaceName);

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = $"someone@{domain}" });

        Assert.Contains("https://login.example.com/authorize", start.AuthorizationUrl, StringComparison.Ordinal);
        Assert.NotEmpty(start.State);

        // The redirect URI the provider is given is this deployment's, not anything a caller chose.
        Assert.Equal("http://studio.test/sign-in/sso/callback", factory.Oidc.LastRedirectUri);
    }

    [Fact]
    public async Task An_address_with_no_connection_is_answered_the_same_way_as_one_with_no_account()
    {
        // This endpoint is unauthenticated. It must not become a way to discover which companies
        // use the platform, or which addresses are registered.
        var unknown = await DiscoverAsync($"nobody@{Guid.NewGuid():N}.example.com");
        var registered = await DiscoverAsync("someone@example.org");

        Assert.False(unknown.Available);
        Assert.False(registered.Available);
        Assert.Null(unknown.WorkspaceName);
        Assert.Null(registered.WorkspaceName);
    }

    [Fact]
    public async Task A_first_sign_in_provisions_an_account_and_a_membership()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);
        var email = $"newcomer-{Guid.NewGuid():N}@{domain}";

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", email, true, "New Comer");

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = email });
        var auth = await PostAsync<AuthResponse>("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(email, auth.User.Email);
        Assert.Contains(auth.User.Workspaces, w => w.WorkspaceId == owner.WorkspaceId);
        Assert.NotEmpty(auth.AccessToken);

        await using var db = factory.CreateDbContext();
        var membership = await db.WorkspaceMembers.AsNoTracking()
            .SingleAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == auth.User.Id);
        Assert.Equal(WorkspaceRole.Host, membership.Role);
    }

    [Fact]
    public async Task The_issued_session_works_against_the_rest_of_the_api()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);
        var email = $"worker-{Guid.NewGuid():N}@{domain}";

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", email, true, "Worker");
        var auth = await SignInAsync(email);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var me = await client.GetAsync("/api/v1/auth/me");
        await factory.EnsureSuccessAsync(me);
    }

    [Fact]
    public async Task A_second_sign_in_returns_the_same_account_even_when_the_address_changed()
    {
        // Accounts are keyed on the provider's subject, never on email: an address can be reassigned
        // inside a company, and matching on it would hand the new holder the old holder's account.
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);
        var subject = $"subject-{Guid.NewGuid():N}";
        var original = $"before-{Guid.NewGuid():N}@{domain}";

        factory.Oidc.Identity = new OidcIdentity(subject, original, true, "Person");
        var first = await SignInAsync(original);

        factory.Oidc.Identity = new OidcIdentity(subject, $"after-{Guid.NewGuid():N}@{domain}", true, "Person");
        var second = await SignInAsync(original);

        Assert.Equal(first.User.Id, second.User.Id);
        Assert.Equal(original, second.User.Email);
    }

    [Fact]
    public async Task An_identity_outside_every_verified_domain_is_refused()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        // The provider authenticated somebody, but not somebody at a domain this workspace proved
        // it owns. Accepting it would let a workspace sign in anybody its provider vouches for.
        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", "outsider@elsewhere.example",
            true, "Outsider");

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = $"a@{domain}" });
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unverified_email_claim_is_refused()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", $"unverified@{domain}",
            EmailVerified: false, "Unverified");

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = $"a@{domain}" });
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_state_value_can_only_be_used_once()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", $"once-{Guid.NewGuid():N}@{domain}",
            true, "Once");

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = $"a@{domain}" });
        await PostAsync<AuthResponse>("/api/v1/auth/sso/callback", new { start.State, Code = "valid-code" });

        var replay = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task An_unknown_state_is_refused_without_reaching_the_provider()
    {
        var before = factory.Oidc.ExchangeCount;

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { State = "made-up", Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
        Assert.Equal(before, factory.Oidc.ExchangeCount);
    }

    [Fact]
    public async Task Turning_a_connection_off_stops_a_sign_in_that_was_already_in_flight()
    {
        // The browser is away at the provider for as long as it takes somebody to type a password.
        // An admin who switches the connection off in that window means it.
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", $"inflight@{domain}", true, "In Flight");
        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = $"a@{domain}" });

        await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(domain, enabled: false));

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task With_provisioning_off_only_people_already_in_the_workspace_may_sign_in()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner, jitProvisioning: false);
        var email = $"stranger-{Guid.NewGuid():N}@{domain}";

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", email, true, "Stranger");

        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = email });
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/callback",
            new { start.State, Code = "valid-code" });

        Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_provider_that_is_unreachable_fails_the_sign_in_and_nothing_else()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);

        factory.Oidc.Failure = new OidcException("discovery unreachable");

        try
        {
            var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/sso/start",
                new { Email = $"a@{domain}" });

            Assert.Equal(ErrorCodes.SsoFailed, await ErrorCodeAsync(response));
        }
        finally
        {
            factory.Oidc.Failure = null;
        }

        // Password sign-in is untouched: an identity provider being down must not take the platform
        // with it.
        var password = await factory.CreateAuthenticatedClientAsync();
        Assert.NotEmpty(password.Auth.AccessToken);
    }

    [Fact]
    public async Task An_account_created_by_single_sign_on_has_no_password_to_guess()
    {
        var owner = await BusinessWorkspaceAsync();
        var domain = await UsableConnectionAsync(owner);
        var email = $"nopassword-{Guid.NewGuid():N}@{domain}";

        factory.Oidc.Identity = new OidcIdentity($"subject-{Guid.NewGuid():N}", email, true, "No Password");
        await SignInAsync(email);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "any-password-at-all"));

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static string Domain(AuthenticatedClient client) => $"ws-{client.WorkspaceId:N}.example.com";

    private static UpsertSsoConnectionRequest Request(
        string domain,
        string? clientSecret = "client-secret",
        string issuer = "https://login.example.com",
        string defaultRole = "Host",
        bool enabled = true,
        bool jitProvisioning = true,
        IReadOnlyList<string>? extraDomains = null) =>
        new(issuer, "client-id", clientSecret, enabled, jitProvisioning, defaultRole,
            [domain, .. extraDomains ?? []]);

    /// <summary>A workspace on a plan that includes single sign-on, promoted the only way it can be.</summary>
    private async Task<AuthenticatedClient> BusinessWorkspaceAsync()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/operations/workspaces/{owner.WorkspaceId}/plan")
        {
            Content = JsonContent.Create(new ChangePlanRequest("Business")),
        };
        request.Headers.Add("X-Internal-Auth", LiveStreamApiFactory.InternalSecret);

        await factory.EnsureSuccessAsync(await factory.CreateClient().SendAsync(request));
        return owner;
    }

    /// <summary>A configured connection with one operator-verified domain. Returns the domain.</summary>
    private async Task<string> UsableConnectionAsync(AuthenticatedClient owner, bool jitProvisioning = true)
    {
        var domain = Domain(owner);

        await PutAsync<SsoConnectionResponse>(owner, $"/api/v1/workspaces/{owner.WorkspaceId}/sso",
            Request(domain, jitProvisioning: jitProvisioning));
        await VerifyDomainAsync(domain);

        return domain;
    }

    private async Task VerifyDomainAsync(string domain)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/operations/sso-domains/{domain}")
        {
            Content = JsonContent.Create(new { Verified = true }),
        };
        request.Headers.Add("X-Internal-Auth", LiveStreamApiFactory.InternalSecret);

        await factory.EnsureSuccessAsync(await factory.CreateClient().SendAsync(request));
    }

    private async Task<AuthResponse> SignInAsync(string email)
    {
        var start = await PostAsync<SsoStartResponse>("/api/v1/auth/sso/start", new { Email = email });
        return await PostAsync<AuthResponse>("/api/v1/auth/sso/callback", new { start.State, Code = "valid-code" });
    }

    private async Task<SsoDiscoveryResponse> DiscoverAsync(string email) =>
        await PostAsync<SsoDiscoveryResponse>("/api/v1/auth/sso/discover", new { Email = email });

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(url, body);
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

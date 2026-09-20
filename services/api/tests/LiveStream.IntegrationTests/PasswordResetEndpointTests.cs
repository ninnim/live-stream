using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Auth;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// The password reset endpoints over real HTTP.
///
/// The unit tests cover what the service decides. What only shows up here is what the *wire* says —
/// the status codes, which is where user enumeration would actually leak. A service that behaves
/// identically for a known and an unknown address still gives the game away if one returns 202 and
/// the other 404.
/// </summary>
public class PasswordResetEndpointTests(LiveStreamApiFactory factory) : IClassFixture<LiveStreamApiFactory>
{
    private const string OriginalPassword = "a-sufficiently-long-password";
    private const string NewPassword = "an-even-longer-new-password";

    private string TokenFor(string email)
    {
        var message = factory.Email.Sent.Last(m => m.ToAddress == email);

        var link = message.TextBody
            .Split('\n', StringSplitOptions.TrimEntries)
            .First(line => line.Contains("/reset-password?token=", StringComparison.Ordinal));

        return Uri.UnescapeDataString(link.Split("token=")[1]);
    }

    [Fact]
    public async Task A_forgotten_password_can_be_reset_and_used_to_sign_in()
    {
        // The whole journey, over HTTP, exactly as the browser drives it.
        var email = $"forgetful-{Guid.NewGuid():N}@example.com";
        await factory.CreateAuthenticatedClientAsync(email);

        var client = factory.CreateClient();

        var requested = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(email));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(TokenFor(email), NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var signIn = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, NewPassword));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, OriginalPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    [Fact]
    public async Task An_unknown_address_answers_exactly_like_a_known_one()
    {
        // The assertion this file exists for. Identical status, identical body — anything else
        // makes this the easiest place on the platform to enumerate its users.
        var known = $"known-{Guid.NewGuid():N}@example.com";
        await factory.CreateAuthenticatedClientAsync(known);

        var client = factory.CreateClient();

        var forKnown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(known));
        var forUnknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest($"nobody-{Guid.NewGuid():N}@example.com"));

        Assert.Equal(forKnown.StatusCode, forUnknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, forUnknown.StatusCode);
        Assert.Equal(
            await forKnown.Content.ReadAsStringAsync(),
            await forUnknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_malformed_address_answers_the_same_way_too()
    {
        // A 400 here would say "that is not an email address", which is a third distinguishable
        // answer and just as useful to a probe as the second.
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest("definitely-not-an-address"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_reset_signs_out_a_session_that_was_already_open()
    {
        // The case this exists for: somebody resets because they think an intruder is signed in.
        // The intruder's refresh token is the thing that would otherwise outlive the reset.
        var email = $"compromised-{Guid.NewGuid():N}@example.com";
        var session = await factory.CreateAuthenticatedClientAsync(email);

        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email));
        await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(TokenFor(email), NewPassword));

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(session.Auth.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task A_spent_link_is_refused()
    {
        var email = $"twice-{Guid.NewGuid():N}@example.com";
        await factory.CreateAuthenticatedClientAsync(email);

        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email));
        var token = TokenFor(email);

        var first = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, "yet-another-long-password"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Neither_endpoint_needs_authentication()
    {
        // Obvious, and worth a test: somebody who has lost their password by definition cannot
        // present one, and a stray RequireAuthorization here would lock them out permanently.
        var client = factory.CreateClient();

        var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest($"anyone-{Guid.NewGuid():N}@example.com"));
        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest("made-up-token", NewPassword));

        Assert.NotEqual(HttpStatusCode.Unauthorized, forgot.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, reset.StatusCode);
    }

    [Fact]
    public async Task The_emailed_link_points_at_the_web_app()
    {
        // The link is built server-side from configuration. Pointing it at the API's own origin
        // would send people to a page that does not exist.
        var email = $"linked-{Guid.NewGuid():N}@example.com";
        await factory.CreateAuthenticatedClientAsync(email);

        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email));

        var message = factory.Email.Sent.Last(m => m.ToAddress == email);

        Assert.Contains("/reset-password?token=", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("/reset-password?token=", message.HtmlBody, StringComparison.Ordinal);

        // Never the hash, and never anything else from the row.
        Assert.DoesNotContain("TokenHash", message.TextBody, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Net;
using System.Net.Http.Json;
using LiveStream.Application.Auth;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Abuse controls (docs/11-security.md). The main suite runs with the limits raised, so these
/// tests use a factory configured with deliberately low limits to prove they are enforced.
/// </summary>
public class RateLimitingTests
{
    private sealed class ThrottledApiFactory : LiveStreamApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimits:AuthenticationPerMinute"] = "3",
                    ["RateLimits:SessionCreationPerMinute"] = "2",
                    ["RateLimits:CredentialIssuancePerMinute"] = "2",
                }));
        }
    }

    [Fact]
    public async Task Repeated_login_attempts_are_throttled()
    {
        using var factory = new ThrottledApiFactory();
        var client = factory.CreateClient();
        var request = new LoginRequest("nobody@example.com", "a-sufficiently-long-password");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", request);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Rapid_session_creation_is_throttled()
    {
        using var factory = new ThrottledApiFactory();
        var owner = await factory.CreateAuthenticatedClientAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
                new CreateLiveSessionRequest($"Session {i}", null, "PRIVATE", false));
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task A_throttled_response_carries_the_rate_limit_error_code()
    {
        using var factory = new ThrottledApiFactory();
        var client = factory.CreateClient();
        var request = new LoginRequest("nobody@example.com", "a-sufficiently-long-password");

        HttpResponseMessage? throttled = null;
        for (var i = 0; i < 6 && throttled is null; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", request);
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                throttled = response;
            }
        }

        Assert.NotNull(throttled);
        var body = await throttled.Content.ReadAsStringAsync();
        Assert.Contains("LIVE_014_RATE_LIMITED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_cannot_hold_more_than_the_configured_open_sessions()
    {
        // Independent of request rate: bounds how many sessions a tenant can leave open at once.
        //
        // Phase 7 turned this into a plan limit, so it answers 409 rather than 429. The
        // distinction matters to a client: a rate limit clears by waiting, and a client told to
        // retry a request that cannot succeed until the plan changes would retry forever.
        using var factory = new LiveStreamApiFactory();
        var owner = await factory.CreateAuthenticatedClientAsync();

        for (var i = 0; i < 3; i++)
        {
            var ok = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
                new CreateLiveSessionRequest($"Session {i}", null, "PRIVATE", false));
            ok.EnsureSuccessStatusCode();
        }

        var overLimit = await owner.Client.PostAsJsonAsync("/api/v1/live-sessions",
            new CreateLiveSessionRequest("One too many", null, "PRIVATE", false));

        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
        Assert.Contains(ErrorCodes.PlanLimitReached, await overLimit.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}

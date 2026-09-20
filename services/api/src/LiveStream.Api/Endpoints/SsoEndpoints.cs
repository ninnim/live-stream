using LiveStream.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// The single sign-on flow, from the sign-in page's point of view
/// (implementation/phase-7: "SSO").
///
/// All three routes are anonymous, because nobody is signed in yet — which is exactly why all three
/// are rate limited under the authentication budget and answer identically for addresses that do
/// and do not exist.
/// </summary>
public static class SsoEndpoints
{
    public static IEndpointRouteBuilder MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth/sso")
            .WithTags("Authentication")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/discover", async (
                [FromBody] SsoDiscoverRequest request,
                SsoService sso,
                CancellationToken cancellationToken) =>
            Results.Ok(await sso.DiscoverAsync(request.Email, cancellationToken)))
            // A POST rather than a GET with the address in the query string: sign-in pages are
            // linked and screenshotted, and an email address does not belong in a URL.
            .WithName("DiscoverSso")
            .WithSummary("Says whether an email address signs in through an identity provider.");

        group.MapPost("/start", async (
                [FromBody] SsoDiscoverRequest request,
                SsoService sso,
                CancellationToken cancellationToken) =>
            Results.Ok(await sso.StartAsync(request.Email, cancellationToken)))
            .WithName("StartSso")
            .WithSummary("Begins a sign-in and returns the provider URL to send the browser to.");

        group.MapPost("/callback", async (
                [FromBody] SsoCallbackRequest request,
                SsoService sso,
                CancellationToken cancellationToken) =>
            Results.Ok(await sso.CompleteAsync(request.State, request.Code, cancellationToken)))
            // The browser lands on the web app, which posts the code here and receives tokens in the
            // response body. Tokens in a redirect URL would be written into browser history and
            // every proxy log between here and there.
            .WithName("CompleteSso")
            .WithSummary("Exchanges an authorization code for a session.");

        return app;
    }
}

public sealed record SsoDiscoverRequest(string Email);

public sealed record SsoCallbackRequest(string State, string Code);

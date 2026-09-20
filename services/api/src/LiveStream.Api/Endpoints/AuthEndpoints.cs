using LiveStream.Api.Security;
using LiveStream.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>Authentication endpoints under <c>/api/v1/auth</c> (docs/09-api-specification.md).</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Auth")
            // Credential endpoints are the classic brute-force target, so they get a tighter limit
            // than the rest of the API (docs/11-security.md abuse controls).
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/register", async (
                [FromBody] RegisterRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            Results.Ok(await authService.RegisterAsync(request, cancellationToken)))
            .AllowAnonymous()
            .WithName("Register")
            .WithSummary("Creates an account and its first workspace.");

        group.MapPost("/login", async (
                [FromBody] LoginRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            Results.Ok(await authService.LoginAsync(request, cancellationToken)))
            .AllowAnonymous()
            .WithName("Login")
            .WithSummary("Exchanges email and password for an access/refresh token pair.");

        group.MapPost("/refresh", async (
                [FromBody] RefreshRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            Results.Ok(await authService.RefreshAsync(request, cancellationToken)))
            .AllowAnonymous()
            .WithName("RefreshToken")
            .WithSummary("Rotates a refresh token and issues a new access token.");

        group.MapPost("/forgot-password", async (
                [FromBody] ForgotPasswordRequest request,
                PasswordResetService passwordReset,
                CancellationToken cancellationToken) =>
            {
                await passwordReset.RequestAsync(request, cancellationToken);

                // Always 202, whatever happened. An unknown address, a suspended account and a
                // mail server that refused the message all answer identically — this is the one
                // unauthenticated endpoint that takes an email address and would otherwise say
                // whether it belongs to somebody.
                return Results.Accepted();
            })
            .AllowAnonymous()
            .WithName("ForgotPassword")
            .WithSummary("Emails a reset link, if the address belongs to an active account.");

        group.MapPost("/reset-password", async (
                [FromBody] ResetPasswordRequest request,
                PasswordResetService passwordReset,
                CancellationToken cancellationToken) =>
            {
                await passwordReset.ResetAsync(request, cancellationToken);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .WithName("ResetPassword")
            .WithSummary("Sets a new password from a reset link, and signs out every session.");

        group.MapPost("/logout", async (
                [FromBody] RefreshRequest? request,
                HttpContext http,
                AuthService authService,
                CancellationToken cancellationToken) =>
            {
                await authService.LogoutAsync(http.User.RequireUserId(), request?.RefreshToken, cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithName("Logout")
            .WithSummary("Revokes the supplied refresh token, or every token for the user when omitted.");

        group.MapGet("/me", async (
                HttpContext http,
                AuthService authService,
                CancellationToken cancellationToken) =>
            Results.Ok(await authService.GetCurrentUserAsync(http.User.RequireUserId(), cancellationToken)))
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .WithSummary("Returns the signed-in user and their workspace memberships.");

        return app;
    }
}

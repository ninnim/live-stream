using System.Security.Cryptography;
using System.Text;
using LiveStream.Application.Auth;
using LiveStream.Application.Governance;
using LiveStream.Application.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Platform operator routes (implementation/phase-7: capacity visibility, tenant limits, SSO).
///
/// Authenticated with the internal shared secret rather than a user token, because none of these
/// belong to a tenant: a plan is what a customer is entitled to, and a domain verification decides
/// whether a workspace may sign in everyone at an email domain. Nobody inside a workspace may do
/// either to themselves.
///
/// Header only — no query-string fallback. The media gateway needs that fallback because it cannot
/// set headers; operator tooling can, and a secret in a URL ends up in logs.
/// </summary>
public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/operations")
            .WithTags("Operations")
            .AllowAnonymous();

        group.MapGet("/capacity", async (
                HttpContext http,
                IOptions<InternalApiOptions> options,
                UsageService usage,
                CancellationToken cancellationToken) =>
            !HasValidSecret(http, options.Value)
                ? Results.NotFound()
                : Results.Ok(await usage.GetPlatformCapacityAsync(cancellationToken)))
            .WithName("GetPlatformCapacity")
            .WithSummary("What this deployment is carrying: sessions, sources, queues, storage, leases.");

        group.MapPut("/workspaces/{workspaceId:guid}/plan", async (
                Guid workspaceId,
                [FromBody] ChangePlanRequest request,
                HttpContext http,
                IOptions<InternalApiOptions> options,
                WorkspaceGovernanceService governance,
                CancellationToken cancellationToken) =>
            !HasValidSecret(http, options.Value)
                ? Results.NotFound()
                : Results.Ok(await governance.ChangePlanAsync(workspaceId, request.Plan, cancellationToken)))
            .WithName("ChangeWorkspacePlan")
            .WithSummary("Moves a workspace between plans. The only way a limit is ever raised.");

        group.MapPut("/sso-domains/{domain}", async (
                string domain,
                [FromBody] VerifyDomainRequest request,
                HttpContext http,
                IOptions<InternalApiOptions> options,
                SsoService sso,
                CancellationToken cancellationToken) =>
            !HasValidSecret(http, options.Value)
                ? Results.NotFound()
                : Results.Ok(await sso.SetDomainVerificationAsync(domain, request.Verified, cancellationToken)))
            .WithName("VerifySsoDomain")
            .WithSummary("Verifies, or withdraws verification of, a claimed email domain.");

        return app;
    }

    /// <summary>
    /// Returns 404 rather than 401 when the secret is wrong: an unauthenticated caller should not
    /// be able to learn that an operations API exists here at all.
    /// </summary>
    private static bool HasValidSecret(HttpContext http, InternalApiOptions options)
    {
        var presented = http.Request.Headers[MediaCallbackEndpoints.SecretHeaderName].FirstOrDefault();

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // Fixed-time comparison so the secret cannot be recovered by timing the endpoint.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(options.SharedSecret));
    }
}

public sealed record VerifyDomainRequest(bool Verified);

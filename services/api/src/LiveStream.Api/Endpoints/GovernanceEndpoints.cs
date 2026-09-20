using LiveStream.Api.Security;
using LiveStream.Application.Governance;
using LiveStream.Application.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Workspace administration: limits, usage, single sign-on, export and erasure
/// (implementation/phase-7-scale-security-and-globalization.md).
///
/// Everything here is scoped to one workspace and authorized against membership in it. Nothing here
/// can raise a workspace above its plan — that is an operator action, and it lives on the internal
/// operations API instead.
/// </summary>
public static class GovernanceEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces/{workspaceId:guid}")
            .WithTags("Workspace")
            .RequireAuthorization();

        group.MapGet("/limits", async (
                Guid workspaceId,
                HttpContext http,
                WorkspaceGovernanceService governance,
                CancellationToken cancellationToken) =>
            Results.Ok(await governance.GetLimitsAsync(workspaceId, http.User.RequireUserId(), cancellationToken)))
            .WithName("GetWorkspaceLimits")
            .WithSummary("Reports the workspace plan, what it allows, and what is actually enforced.");

        group.MapPut("/limits", async (
                Guid workspaceId,
                [FromBody] UpdateWorkspaceLimitsRequest request,
                HttpContext http,
                WorkspaceGovernanceService governance,
                CancellationToken cancellationToken) =>
            Results.Ok(await governance.UpdateLimitsAsync(workspaceId, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("UpdateWorkspaceLimits")
            .WithSummary("Replaces the workspace's own limits. These may tighten the plan, never loosen it.");

        group.MapGet("/usage", async (
                Guid workspaceId,
                DateTimeOffset? from,
                DateTimeOffset? to,
                HttpContext http,
                UsageService usage,
                CancellationToken cancellationToken) =>
            Results.Ok(await usage.GetWorkspaceUsageAsync(workspaceId, http.User.RequireUserId(), from, to,
                cancellationToken)))
            .WithName("GetWorkspaceUsage")
            .WithSummary("Capacity, measured usage, and an estimated cost for a period. Defaults to 30 days.");

        group.MapGet("/export", async (
                Guid workspaceId,
                HttpContext http,
                WorkspaceGovernanceService governance,
                CancellationToken cancellationToken) =>
            Results.Ok(await governance.ExportAsync(workspaceId, http.User.RequireUserId(), cancellationToken)))
            // Shares the destination-management budget: an export is an expensive read, and one a
            // caller has no reason to repeat quickly.
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("ExportWorkspace")
            .WithSummary("Everything the workspace holds, as one document. Carries no secrets.");

        group.MapDelete("/", async (
                Guid workspaceId,
                [FromBody] EraseWorkspaceRequest request,
                HttpContext http,
                WorkspaceGovernanceService governance,
                CancellationToken cancellationToken) =>
            Results.Ok(await governance.EraseAsync(workspaceId, http.User.RequireUserId(), request.Confirmation,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("EraseWorkspace")
            .WithSummary("Deletes the workspace, its sessions, and its recorded media. Not recoverable.");

        MapSsoAdministration(group);

        return app;
    }

    private static void MapSsoAdministration(RouteGroupBuilder group)
    {
        group.MapGet("/sso", async (
                Guid workspaceId,
                HttpContext http,
                Application.Auth.SsoService sso,
                CancellationToken cancellationToken) =>
            {
                var connection = await sso.GetConnectionAsync(workspaceId, http.User.RequireUserId(),
                    cancellationToken);

                return connection is null ? Results.NoContent() : Results.Ok(connection);
            })
            .WithName("GetSsoConnection")
            .WithSummary("The workspace's identity provider, if one is configured. Never returns the secret.");

        group.MapPut("/sso", async (
                Guid workspaceId,
                [FromBody] UpsertSsoConnectionRequest request,
                HttpContext http,
                Application.Auth.SsoService sso,
                CancellationToken cancellationToken) =>
            Results.Ok(await sso.UpsertConnectionAsync(workspaceId, http.User.RequireUserId(), request,
                cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("UpsertSsoConnection")
            .WithSummary("Configures single sign-on. Claimed domains stay inert until an operator verifies them.");

        group.MapDelete("/sso", async (
                Guid workspaceId,
                HttpContext http,
                Application.Auth.SsoService sso,
                CancellationToken cancellationToken) =>
            {
                await sso.DeleteConnectionAsync(workspaceId, http.User.RequireUserId(), cancellationToken);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimitPolicies.DestinationManagement)
            .WithName("DeleteSsoConnection")
            .WithSummary("Removes the identity provider. People who signed in through it keep their accounts.");
    }
}

/// <summary>
/// Erasure needs the workspace name typed back. Nothing it removes can be recovered, and a stray
/// DELETE on a guessed id must not be enough.
/// </summary>
public sealed record EraseWorkspaceRequest(string Confirmation);

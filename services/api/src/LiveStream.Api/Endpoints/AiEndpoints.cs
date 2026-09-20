using LiveStream.Api.Security;
using LiveStream.Application.Ai;
using LiveStream.Application.Ai.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// AI operations (docs/13-ai-features.md, implementation/phase-6-ai-live-operations.md).
///
/// Every route here queues or reads work. None of them call a model, so none of them can be slow
/// because a provider is slow — which is what "asynchronous" in the phase's acceptance criteria
/// has to mean in practice.
/// </summary>
public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        // Deployment-wide rather than per-session: the UI needs to know what exists before it has a
        // session to ask about.
        app.MapGet("/api/v1/ai/capabilities", (AiJobService ai) => Results.Ok(ai.Describe()))
            .RequireAuthorization()
            .WithTags("AI")
            .WithName("GetAiCapabilities")
            .WithSummary("Reports whether AI is configured and which features are switched on.");

        var group = app.MapGroup("/api/v1/live-sessions/{id:guid}/ai-jobs")
            .WithTags("AI")
            .RequireAuthorization();

        group.MapGet("/", async (
                Guid id,
                HttpContext http,
                AiJobService ai,
                CancellationToken cancellationToken) =>
            Results.Ok(await ai.ListAsync(id, http.User.RequireUserId(), cancellationToken)))
            .WithName("ListAiJobs")
            .WithSummary("Lists this session's AI jobs, newest first, with their results and cost.");

        group.MapPost("/", async (
                Guid id,
                [FromBody] RequestAiJobRequest request,
                HttpContext http,
                AiJobService ai,
                CancellationToken cancellationToken) =>
            {
                var job = await ai.RequestAsync(id, http.User.RequireUserId(), request, cancellationToken);
                return Results.Accepted($"/api/v1/live-sessions/{id}/ai-jobs/{job.Id}", job);
            })
            // Shares the credential-issuance limit: both are cheap to ask for and expensive to
            // serve, and this one spends money per request.
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("RequestAiJob")
            .WithSummary("Queues an AI job. Returns immediately; the work runs in the background.");

        group.MapDelete("/{jobId:guid}", async (
                Guid id,
                Guid jobId,
                HttpContext http,
                AiJobService ai,
                CancellationToken cancellationToken) =>
            Results.Ok(await ai.CancelAsync(id, jobId, http.User.RequireUserId(), cancellationToken)))
            .RequireRateLimiting(RateLimitPolicies.CredentialIssuance)
            .WithName("CancelAiJob")
            .WithSummary("Withdraws a queued AI job. A job already running is left to finish.");

        return app;
    }
}

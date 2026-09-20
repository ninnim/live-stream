using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.Health;

/// <summary>
/// Whether this instance is still willing to be sent traffic.
///
/// One flag, flipped once, at the start of shutdown. It is what turns a rolling deploy from "some
/// requests get a connection reset" into "the load balancer stopped choosing this instance a few
/// seconds ago" (implementation/phase-7: autoscaling and independent scaling of components).
/// </summary>
public sealed class ReadinessState
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void BeginDraining() => _draining = true;
}

/// <summary>
/// Fails readiness — and only readiness — while the instance is draining.
///
/// Liveness stays healthy throughout: an orchestrator that saw liveness fail would kill the process
/// immediately, which is the opposite of a graceful shutdown.
/// </summary>
public sealed class DrainingHealthCheck(ReadinessState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsDraining
            ? HealthCheckResult.Unhealthy("This instance is shutting down and is no longer accepting work.")
            : HealthCheckResult.Healthy("Accepting traffic."));
}

/// <summary>
/// The database, which this instance genuinely cannot serve a request without. It is therefore part
/// of readiness, unlike the media gateway.
/// </summary>
public sealed class DatabaseHealthCheck(IAppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The cheapest query that proves a round trip: no table scan, no locks.
            await db.Workspaces.AsNoTracking().Select(w => w.Id).FirstOrDefaultAsync(cancellationToken);
            return HealthCheckResult.Healthy("Reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database is not reachable.", ex);
        }
    }
}

/// <summary>
/// The media gateway. Reported, but deliberately <em>not</em> part of readiness.
///
/// Taking every API instance out of the load balancer because the media plane is down would replace
/// a degraded platform with an unreachable one: the control plane can still list sessions, show
/// health, end broadcasts, and tell people what is wrong. So this check is reported on the
/// dependency endpoint and shows as Degraded, and the session lifecycle surfaces the real failure
/// where it belongs — on the session being prepared.
/// </summary>
public sealed class MediaGatewayHealthCheck(IMediaGateway gateway) : IHealthCheck
{
    /// <summary>
    /// A path name no session can own. The gateway answers "unknown path" for it, which proves the
    /// control API is reachable without touching anyone's broadcast.
    /// </summary>
    private const string ProbePath = "health-probe";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await gateway.GetPathStateAsync(ProbePath, cancellationToken);
            return HealthCheckResult.Healthy($"{gateway.ProviderName} control API is reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"{gateway.ProviderName} control API is not reachable.", ex);
        }
    }
}

/// <summary>
/// Turns readiness off, then pauses, before anything else shuts down.
///
/// Hosted services are stopped in reverse registration order, so this one is registered last and
/// therefore stops first. The pause is not idle time: it is the window in which the load balancer
/// notices readiness has gone and stops sending new requests, while this instance still serves the
/// ones already in flight.
/// </summary>
public sealed class GracefulShutdownService(
    ReadinessState state,
    IOptions<RuntimeOptions> options,
    ILogger<GracefulShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        state.BeginDraining();

        var drain = options.Value.ShutdownDrain;

        logger.LogInformation("Draining: readiness is now failing; waiting {DrainSeconds}s before shutdown",
            (int)drain.TotalSeconds);

        if (drain <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(drain, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown timeout won. Nothing to do: everything below is stopping anyway.
        }
    }
}

/// <summary>Renders health results as JSON, so an operator sees which check failed and why.</summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),

                    // The description is ours; the exception message is not, and could carry a
                    // connection string. Only the description is published.
                    description = entry.Value.Description,
                    durationMs = (int)entry.Value.Duration.TotalMilliseconds,
                }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}

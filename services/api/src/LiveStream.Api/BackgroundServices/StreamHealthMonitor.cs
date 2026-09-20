using LiveStream.Application.Common;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// Periodically reconciles every active session against the media plane.
///
/// This is what makes reconnect and health monitoring work without trusting the client: it detects
/// lost ingest, drives RECONNECTING inside the bounded recovery window, promotes STARTING to LIVE
/// when media arrives, and fails sessions that never recover
/// (MASTER_BLUEPRINT.md §11, docs/12-observability-and-reliability.md).
///
/// It runs on one instance at a time — see <see cref="LeasedBackgroundService"/> — because two
/// instances reconciling one session would race each other into the recovery window.
/// </summary>
public sealed class StreamHealthMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    IOptions<LiveSessionOptions> options,
    ILogger<StreamHealthMonitor> logger)
    : LeasedBackgroundService(scopeFactory, runtimeOptions, logger)
{
    private readonly LiveSessionOptions _options = options.Value;

    protected override string LeaseName => RuntimeLease.Names.StreamHealth;

    protected override string LoopName => "Stream health monitor";

    protected override TimeSpan Interval => _options.HealthPollInterval;

    protected override string StartupDetail => $"recoveryWindow={_options.RecoveryWindowSeconds}s";

    protected override Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<LiveSessionReconciler>().ReconcileActiveSessionsAsync(cancellationToken);
}

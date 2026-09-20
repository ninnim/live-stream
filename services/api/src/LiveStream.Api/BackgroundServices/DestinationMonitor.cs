using LiveStream.Application.Common;
using LiveStream.Application.Distribution;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// Periodically reconciles every active destination against the egress relay.
///
/// Deliberately a separate service from <see cref="StreamHealthMonitor"/> rather than another step
/// inside it. Distribution runs at its own cadence, and — more importantly — a distribution pass
/// that throws must not be able to stop session health reconciliation, which is what keeps
/// reconnect working. Two loops, two blast radii, and two leases: the instance that reconciles
/// destinations need not be the one reconciling sessions.
/// </summary>
public sealed class DestinationMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    IOptions<DistributionOptions> options,
    ILogger<DestinationMonitor> logger)
    : LeasedBackgroundService(scopeFactory, runtimeOptions, logger)
{
    private readonly DistributionOptions _options = options.Value;

    protected override string LeaseName => RuntimeLease.Names.Destinations;

    protected override string LoopName => "Destination monitor";

    protected override TimeSpan Interval => _options.ReconcileInterval;

    protected override string StartupDetail => $"maxRetries={_options.MaxRetryAttempts}";

    protected override Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<DestinationOrchestrator>().ReconcileActiveDestinationsAsync(cancellationToken);
}

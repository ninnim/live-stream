using LiveStream.Application.Common;
using LiveStream.Application.Sources;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// Polls the media plane for each contributing device and keeps the control room's view of them
/// honest.
///
/// A third loop rather than extra work inside the session monitor, for the same reason the
/// destination monitor is separate: presence for a borrowed phone must never be able to interfere
/// with the reconciliation that keeps a broadcast alive.
/// </summary>
public sealed class SourcePresenceMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    IOptions<LiveSessionOptions> sessionOptions,
    IOptions<SourceOptions> sourceOptions,
    ILogger<SourcePresenceMonitor> logger)
    : LeasedBackgroundService(scopeFactory, runtimeOptions, logger)
{
    private readonly SourceOptions _options = sourceOptions.Value;

    protected override string LeaseName => RuntimeLease.Names.SourcePresence;

    protected override string LoopName => "Source presence monitor";

    // Shares the session health cadence: a source dropping is the same class of event as ingest
    // dropping, and the operator should learn about both at the same speed.
    protected override TimeSpan Interval => sessionOptions.Value.HealthPollInterval;

    protected override string StartupDetail => $"graceSeconds={_options.SourcePresenceGraceSeconds}";

    protected override Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<SourceReconciler>().ReconcileActiveSourcesAsync(cancellationToken);
}

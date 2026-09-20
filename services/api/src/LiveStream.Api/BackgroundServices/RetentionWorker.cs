using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// Deletes recordings whose workspace retention window has elapsed
/// (implementation/phase-7: "Compliance/retention controls").
///
/// A retention policy that is only written down is not a retention policy — this loop is what makes
/// it true of the bytes on disk. It runs hourly rather than continuously because the policy is
/// measured in days, and it deletes a bounded batch per pass so that tightening a policy from a
/// year to a week drains steadily instead of hammering storage in one go.
/// </summary>
public sealed class RetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    IOptions<GovernanceOptions> options,
    ILogger<RetentionWorker> logger)
    : LeasedBackgroundService(scopeFactory, runtimeOptions, logger)
{
    private readonly GovernanceOptions _options = options.Value;

    protected override string LeaseName => RuntimeLease.Names.Retention;

    protected override string LoopName => "Retention worker";

    protected override bool Enabled => _options.RetentionEnabled;

    protected override TimeSpan Interval => _options.RetentionSweepInterval;

    protected override string StartupDetail => $"batchSize={_options.RetentionBatchSize}";

    protected override Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RetentionService>().SweepAsync(cancellationToken);
}

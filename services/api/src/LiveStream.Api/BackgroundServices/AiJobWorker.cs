using LiveStream.Application.Ai;
using LiveStream.Application.Common;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// Runs queued AI jobs.
///
/// A fourth loop, entirely separate from the three that keep broadcasts alive. That separation is
/// Phase 6's last acceptance criterion made structural: this loop can stall, throw, or spend
/// minutes inside one request, and the media path never notices.
///
/// It also stays out of the way when there is nothing to do — an unconfigured deployment never
/// starts the timer at all — and it holds a lease, because two instances running the same job
/// would pay for it twice.
/// </summary>
public sealed class AiJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    IOptions<AiOptions> options,
    ILogger<AiJobWorker> logger)
    : LeasedBackgroundService(scopeFactory, runtimeOptions, logger)
{
    private readonly AiOptions _options = options.Value;

    protected override string LeaseName => RuntimeLease.Names.AiJobs;

    protected override string LoopName => "AI job worker";

    protected override bool Enabled => _options.Enabled;

    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds));

    protected override string StartupDetail =>
        $"maxConcurrent={_options.MaxConcurrentJobs} model={_options.Model}";

    protected override Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<AiJobRunner>().RunPendingAsync(cancellationToken);
}

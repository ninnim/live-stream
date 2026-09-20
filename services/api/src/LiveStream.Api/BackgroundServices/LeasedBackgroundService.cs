using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.BackgroundServices;

/// <summary>
/// A background loop that runs on exactly one instance at a time.
///
/// Every loop in this platform is a singleton by nature: two instances reconciling the same session
/// race each other, two instances running the same AI job pay for it twice, and two instances
/// sweeping retention delete the same media twice. Before Phase 7 that was guaranteed by there
/// being one instance. Scaling the API to several replicas removes that guarantee, so each pass now
/// runs only while this instance holds the loop's lease
/// (implementation/phase-7: "The platform can scale components independently").
///
/// Three rules the subclasses inherit, all of them load-bearing:
///
/// <list type="bullet">
/// <item>An instance whose role does not include background work never starts the loop at all,
/// which is what makes an API-only replica cheap to add.</item>
/// <item>A pass that throws is logged and the loop keeps ticking. A dead loop is a feature that
/// silently stops working rather than an error anybody sees.</item>
/// <item>The lease is released on shutdown, so a rolling deploy hands work over in seconds instead
/// of leaving it idle until the lease expires.</item>
/// </list>
/// </summary>
public abstract class LeasedBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<RuntimeOptions> runtimeOptions,
    ILogger logger) : BackgroundService
{
    /// <summary>Upper bound on how long after startup the first pass runs. See <see cref="FirstPassDelay"/>.</summary>
    private static readonly TimeSpan MaxFirstPassDelay = TimeSpan.FromSeconds(15);

    protected RuntimeOptions Runtime { get; } = runtimeOptions.Value;

    /// <summary>Name of the lease this loop holds. One per loop, so work can spread across instances.</summary>
    protected abstract string LeaseName { get; }

    protected abstract TimeSpan Interval { get; }

    /// <summary>Human-readable name for logs.</summary>
    protected abstract string LoopName { get; }

    /// <summary>Lets a subclass decline to start — an unconfigured feature, or a disabled one.</summary>
    protected virtual bool Enabled => true;

    /// <summary>Extra detail for the start log line, e.g. the tunables the loop is running with.</summary>
    protected virtual string StartupDetail => string.Empty;

    /// <summary>
    /// How long to wait before the first pass, as opposed to between passes.
    ///
    /// A periodic timer fires after one full interval, so a loop that ticks hourly — retention does
    /// — would do nothing for an hour after every restart, and on a deployment that restarts more
    /// often than that it would never run at all.
    /// </summary>
    private TimeSpan FirstPassDelay => Interval < MaxFirstPassDelay ? Interval : MaxFirstPassDelay;

    /// <summary>One pass. Resolve services from <paramref name="services"/>, which is a fresh scope.</summary>
    protected abstract Task RunPassAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Runtime.RunsBackgroundWork)
        {
            logger.LogInformation("{Loop} not started: this instance runs role {Role}", LoopName, Runtime.Role);
            return;
        }

        if (!Enabled)
        {
            logger.LogInformation("{Loop} not started: it is switched off for this deployment", LoopName);
            return;
        }

        logger.LogInformation("{Loop} started; interval={IntervalSeconds}s instance={InstanceId} {Detail}",
            LoopName, (int)Interval.TotalSeconds, Runtime.InstanceId, StartupDetail);

        var state = new LoopState();

        try
        {
            await Task.Delay(FirstPassDelay, stoppingToken);
            await TickAsync(state, stoppingToken);

            using var timer = new PeriodicTimer(Interval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(state, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }

        if (state.IsLeader)
        {
            await ReleaseAsync();
        }

        logger.LogInformation("{Loop} stopped", LoopName);
    }

    private async Task TickAsync(LoopState state, CancellationToken stoppingToken)
    {
        try
        {
            // A fresh scope per tick keeps each pass on its own DbContext and change tracker.
            await using var scope = scopeFactory.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseCoordinator>();

            if (!await leases.TryAcquireAsync(LeaseName, stoppingToken))
            {
                if (state.IsLeader)
                {
                    logger.LogInformation("{Loop} stood down: another instance holds the lease", LoopName);
                    state.IsLeader = false;
                }

                return;
            }

            state.IsLeader = true;
            await RunPassAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Loop} pass failed", LoopName);
        }
    }

    /// <summary>
    /// Hands the lease back on shutdown, on a fresh token: the loop's own token is already
    /// cancelled by the time we get here, so releasing on it would cancel the release.
    /// </summary>
    private async Task ReleaseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseCoordinator>();
            await leases.ReleaseAsync(LeaseName, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Loop} could not release its lease; it will expire instead", LoopName);
        }
    }

    private sealed class LoopState
    {
        public bool IsLeader { get; set; }
    }
}

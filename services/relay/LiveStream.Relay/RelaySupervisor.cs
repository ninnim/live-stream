using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace LiveStream.Relay;

/// <summary>
/// Owns every running relay, keyed by destination.
///
/// Registered as a singleton and disposed with the host, so shutting the service down kills its
/// encoder processes rather than orphaning them.
/// </summary>
public sealed class RelaySupervisor(
    IOptions<RelayServiceOptions> options,
    EncoderProbe encoderProbe,
    ISourceInspector sourceInspector,
    ILoggerFactory loggerFactory,
    ILogger<RelaySupervisor> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, RelayWorker> _workers = new();
    private readonly RelayServiceOptions _options = options.Value;

    public bool TryStart(StartRelayRequest request, out RelayStateResponse? state, out string? failureReason)
    {
        state = null;
        failureReason = null;

        // Idempotent by contract: the control plane retries starts, and restarting a healthy relay
        // would interrupt a stream that is working.
        if (_workers.TryGetValue(request.DestinationId, out var existing))
        {
            state = existing.Snapshot();
            return true;
        }

        if (_workers.Count >= _options.MaxConcurrentRelays)
        {
            failureReason = "The relay is at capacity.";
            logger.LogWarning("Refusing relay for {DestinationId}: at capacity ({Max})",
                request.DestinationId, _options.MaxConcurrentRelays);
            return false;
        }

        var worker = new RelayWorker(request, _options, encoderProbe.Selected, sourceInspector,
            loggerFactory.CreateLogger($"Relay.{request.DestinationId}"));

        if (!_workers.TryAdd(request.DestinationId, worker))
        {
            // Lost a race with a concurrent start for the same destination; the winner is correct.
            state = _workers[request.DestinationId].Snapshot();
            return true;
        }

        worker.Start();
        state = worker.Snapshot();

        logger.LogInformation("Relay started for {DestinationId} provider={Provider} session={SessionId}",
            request.DestinationId, request.Provider, request.LiveSessionId);

        return true;
    }

    public async Task<bool> StopAsync(Guid destinationId)
    {
        if (!_workers.TryRemove(destinationId, out var worker))
        {
            return false;
        }

        await worker.DisposeAsync();
        logger.LogInformation("Relay stopped for {DestinationId}", destinationId);
        return true;
    }

    public RelayStateResponse? Get(Guid destinationId) =>
        _workers.TryGetValue(destinationId, out var worker) ? worker.Snapshot() : null;

    public IReadOnlyList<RelayStateResponse> List() =>
        _workers.Values.Select(worker => worker.Snapshot()).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync();
        }

        _workers.Clear();
    }
}

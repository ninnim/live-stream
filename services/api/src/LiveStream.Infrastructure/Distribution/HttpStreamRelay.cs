using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LiveStream.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// Talks to the egress relay service over its private HTTP API.
///
/// The relay is a separate process for the same reason the media gateway is: encoding is a media
/// plane concern, and running encoder processes inside the API host would tie control-plane
/// availability to how many destinations happen to be live (ai/architecture-rules.md).
/// </summary>
public sealed class HttpStreamRelay(HttpClient client, ILogger<HttpStreamRelay> logger) : IStreamRelay
{
    public async Task<RelayStartResult> StartAsync(RelayStartRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("relays", new StartRelayPayload(
                request.DestinationId,
                request.LiveSessionId,
                request.MediaPathName,
                request.SourceCredential,
                request.TargetUrl,
                request.TargetStreamKey,
                request.Provider), cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var state = await response.Content.ReadFromJsonAsync<RelayStatePayload>(cancellationToken);
                return new RelayStartResult(true, state?.ToState());
            }

            // Only the status code is recorded. The request body contained a stream key, and an
            // error response that echoes the request would put it straight into the log.
            logger.LogWarning("Relay refused destination {DestinationId}: {Status}",
                request.DestinationId, (int)response.StatusCode);

            return new RelayStartResult(false, null,
                $"The relay refused the destination ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Relay unreachable while starting destination {DestinationId}",
                request.DestinationId);

            return new RelayStartResult(false, null, "The relay service is unavailable.");
        }
    }

    public async Task StopAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync($"relays/{destinationId}", cancellationToken);

        // Idempotent by contract: a relay that is already gone is the desired end state.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            logger.LogWarning("Relay stop for destination {DestinationId} returned {Status}",
                destinationId, (int)response.StatusCode);
        }
    }

    public async Task<RelayState?> GetAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"relays/{destinationId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<RelayStatePayload>(cancellationToken);
        return payload?.ToState();
    }

    public async Task<IReadOnlyList<RelayState>> ListAsync(CancellationToken cancellationToken)
    {
        var payload = await client.GetFromJsonAsync<List<RelayStatePayload>>("relays", cancellationToken);
        return payload?.Select(p => p.ToState()).ToList() ?? [];
    }

    private sealed record StartRelayPayload(
        [property: JsonPropertyName("destinationId")] Guid DestinationId,
        [property: JsonPropertyName("liveSessionId")] Guid LiveSessionId,
        [property: JsonPropertyName("mediaPathName")] string MediaPathName,
        [property: JsonPropertyName("sourceCredential")] string SourceCredential,
        [property: JsonPropertyName("targetUrl")] string TargetUrl,
        [property: JsonPropertyName("targetStreamKey")] string TargetStreamKey,
        [property: JsonPropertyName("provider")] string Provider);

    private sealed record RelayStatePayload(
        [property: JsonPropertyName("destinationId")] Guid DestinationId,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
        [property: JsonPropertyName("connectedAt")] DateTimeOffset? ConnectedAt,
        [property: JsonPropertyName("bytesSent")] long BytesSent,
        [property: JsonPropertyName("restartCount")] int RestartCount,
        [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode,
        [property: JsonPropertyName("lastErrorMessage")] string? LastErrorMessage,
        [property: JsonPropertyName("lastErrorAt")] DateTimeOffset? LastErrorAt)
    {
        public RelayState ToState() => new(
            DestinationId,
            Enum.TryParse<RelayPhase>(Phase, ignoreCase: true, out var phase) ? phase : RelayPhase.Failed,
            StartedAt,
            ConnectedAt,
            BytesSent,
            RestartCount,
            LastErrorCode,
            LastErrorMessage,
            LastErrorAt);
    }
}

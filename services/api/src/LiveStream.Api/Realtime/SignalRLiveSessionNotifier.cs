using LiveStream.Application.Abstractions;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace LiveStream.Api.Realtime;

/// <summary>
/// Publishes session events to the SignalR group for a session. Event names match
/// docs/09-api-specification.md so the studio contract is documented in one place.
/// </summary>
public sealed class SignalRLiveSessionNotifier(IHubContext<LiveSessionHub> hub) : ILiveSessionNotifier
{
    public Task SessionStateChangedAsync(LiveSessionStatusResponse status, CancellationToken cancellationToken) =>
        Group(status.Id).SendAsync("sessionStateChanged", status, cancellationToken);

    public Task HealthUpdatedAsync(Guid liveSessionId, LiveSessionHealthResponse health,
        CancellationToken cancellationToken) =>
        Group(liveSessionId).SendAsync("healthUpdated", liveSessionId, health, cancellationToken);

    public Task ViewerCountUpdatedAsync(Guid liveSessionId, int viewerCount, CancellationToken cancellationToken) =>
        Group(liveSessionId).SendAsync("viewerCountUpdated", liveSessionId, viewerCount, cancellationToken);

    public Task RecordingStateChangedAsync(Guid liveSessionId, RecordingResponse recording,
        CancellationToken cancellationToken) =>
        Group(liveSessionId).SendAsync("recordingStateChanged", liveSessionId, recording, cancellationToken);

    public Task ErrorRaisedAsync(Guid liveSessionId, string errorCode, string message,
        CancellationToken cancellationToken) =>
        Group(liveSessionId).SendAsync("errorRaised", liveSessionId, new { errorCode, message }, cancellationToken);

    public Task DestinationStateChangedAsync(DestinationStatusResponse destination,
        CancellationToken cancellationToken) =>
        Group(destination.LiveSessionId).SendAsync("destinationStateChanged", destination, cancellationToken);

    public Task SourceStateChangedAsync(SourceStatusResponse source, CancellationToken cancellationToken) =>
        Group(source.LiveSessionId).SendAsync("sourceStateChanged", source, cancellationToken);

    private IClientProxy Group(Guid liveSessionId) => hub.Clients.Group(LiveSessionHub.GroupName(liveSessionId));
}

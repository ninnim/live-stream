using System.Text.Json;
using System.Collections.Concurrent;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Application.Sources.Contracts;

namespace LiveStream.TestSupport;

/// <summary>Controllable clock so lifecycle timeouts and recovery windows can be tested deterministically.</summary>
public sealed class TestClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public void Set(DateTimeOffset value) => UtcNow = value;
}

/// <summary>
/// In-memory media plane. Lets tests drive exactly the conditions that matter for reliability:
/// publisher present or absent, bytes flowing at a chosen rate, viewers attached, gateway down.
/// </summary>
public sealed class FakeMediaGateway : IMediaGateway
{
    private readonly ConcurrentDictionary<string, MediaPathState> _paths = new();

    public string ProviderName => "fake";

    /// <summary>When set, provisioning fails with this reason — used to test PREPARE failure paths.</summary>
    public string? ProvisionFailureReason { get; set; }

    /// <summary>When true, every state lookup throws, simulating an unreachable gateway.</summary>
    public bool ThrowOnStateLookup { get; set; }

    public List<string> ReleasedPaths { get; } = [];

    /// <summary>Paths whose publisher was severed, so revocation can be asserted on.</summary>
    public List<string> KickedPaths { get; } = [];

    public Task<MediaPathProvisionResult> ProvisionPathAsync(MediaPathRequest request,
        CancellationToken cancellationToken)
    {
        var endpoints = DescribeEndpoints(request.MediaPathName);

        if (ProvisionFailureReason is not null)
        {
            return Task.FromResult(new MediaPathProvisionResult(false, endpoints, ProvisionFailureReason));
        }

        _paths.TryAdd(request.MediaPathName, new MediaPathState(request.MediaPathName, false, false, 0, 0, [], null, null));
        return Task.FromResult(new MediaPathProvisionResult(true, endpoints));
    }

    public Task ReleasePathAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        ReleasedPaths.Add(mediaPathName);
        _paths.TryRemove(mediaPathName, out _);
        return Task.CompletedTask;
    }

    public Task<MediaPathState?> GetPathStateAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        if (ThrowOnStateLookup)
        {
            throw new HttpRequestException("Media gateway unreachable (simulated).");
        }

        _paths.TryGetValue(mediaPathName, out var state);
        return Task.FromResult(state);
    }

    /// <summary>
    /// Whether this deployment offers external encoder ingest. Settable so a test can assert the
    /// off case, which is the default in a real deployment (ADR 0022).
    /// </summary>
    public bool ExternalIngestEnabled { get; set; } = true;

    public MediaEndpoints DescribeEndpoints(string mediaPathName) => new(
        "WHIP",
        $"https://media.test/{mediaPathName}/whip",
        $"https://media.test/{mediaPathName}/index.m3u8",
        $"https://media.test/{mediaPathName}/whep",
        ExternalIngestEnabled ? "rtmp://media.test:1935" : null,
        ExternalIngestEnabled ? "srt://media.test:8890" : null);

    // -------------------------------------------------------------------------------------
    // Test controls
    // -------------------------------------------------------------------------------------

    /// <summary>Simulates a broadcaster publishing into the path.</summary>
    public void ConnectPublisher(string mediaPathName, long bytesReceived = 0, int viewers = 0) =>
        _paths[mediaPathName] = new MediaPathState(mediaPathName, true, true, bytesReceived, viewers,
            ["H264", "Opus"], DateTimeOffset.UtcNow, "webRTCSession");

    /// <summary>Simulates the broadcaster's transport dropping while the path still exists.</summary>
    public void DisconnectPublisher(string mediaPathName)
    {
        var bytes = _paths.TryGetValue(mediaPathName, out var existing) ? existing.BytesReceived : 0;
        _paths[mediaPathName] = new MediaPathState(mediaPathName, false, false, bytes, 0, [], null, null);
    }

    /// <summary>Removes the path entirely, as if the gateway forgot it.</summary>
    public void RemovePath(string mediaPathName) => _paths.TryRemove(mediaPathName, out _);

    /// <summary>
    /// Severs the publisher, exactly as revoking a device must. The path stays, so the difference
    /// between "device disconnected" and "path gone" is preserved.
    /// </summary>
    public Task<bool> KickPublisherAsync(string mediaPathName, CancellationToken cancellationToken)
    {
        KickedPaths.Add(mediaPathName);

        if (!_paths.TryGetValue(mediaPathName, out var existing) || !existing.PublisherConnected)
        {
            return Task.FromResult(false);
        }

        _paths[mediaPathName] = existing with { Ready = false, PublisherConnected = false, ReaderCount = 0 };
        return Task.FromResult(true);
    }
}

/// <summary>Captures published realtime events so tests can assert on the studio's contract.</summary>
public sealed class RecordingNotifier : ILiveSessionNotifier
{
    public List<LiveSessionStatusResponse> StateChanges { get; } = [];

    public List<(Guid SessionId, LiveSessionHealthResponse Health)> HealthUpdates { get; } = [];

    public List<(Guid SessionId, int ViewerCount)> ViewerCounts { get; } = [];

    public List<(Guid SessionId, RecordingResponse Recording)> RecordingChanges { get; } = [];

    public List<(Guid SessionId, string ErrorCode, string Message)> Errors { get; } = [];

    public List<DestinationStatusResponse> DestinationChanges { get; } = [];

    public List<SourceStatusResponse> SourceChanges { get; } = [];

    public Task SessionStateChangedAsync(LiveSessionStatusResponse status, CancellationToken cancellationToken)
    {
        StateChanges.Add(status);
        return Task.CompletedTask;
    }

    public Task HealthUpdatedAsync(Guid liveSessionId, LiveSessionHealthResponse health,
        CancellationToken cancellationToken)
    {
        HealthUpdates.Add((liveSessionId, health));
        return Task.CompletedTask;
    }

    public Task ViewerCountUpdatedAsync(Guid liveSessionId, int viewerCount, CancellationToken cancellationToken)
    {
        ViewerCounts.Add((liveSessionId, viewerCount));
        return Task.CompletedTask;
    }

    public Task RecordingStateChangedAsync(Guid liveSessionId, RecordingResponse recording,
        CancellationToken cancellationToken)
    {
        RecordingChanges.Add((liveSessionId, recording));
        return Task.CompletedTask;
    }

    public Task ErrorRaisedAsync(Guid liveSessionId, string errorCode, string message,
        CancellationToken cancellationToken)
    {
        Errors.Add((liveSessionId, errorCode, message));
        return Task.CompletedTask;
    }

    public Task DestinationStateChangedAsync(DestinationStatusResponse destination,
        CancellationToken cancellationToken)
    {
        DestinationChanges.Add(destination);
        return Task.CompletedTask;
    }

    public Task SourceStateChangedAsync(SourceStatusResponse source, CancellationToken cancellationToken)
    {
        SourceChanges.Add(source);
        return Task.CompletedTask;
    }
}

/// <summary>Recording store whose contents tests control directly.</summary>
public sealed class FakeRecordingStore : IRecordingStore
{
    public StoredRecording? Stored { get; set; }

    public Exception? ThrowOnInspect { get; set; }

    /// <summary>Storage keys retention or erasure deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    public Exception? ThrowOnDelete { get; set; }

    public string BuildStorageKey(string mediaPathName) => mediaPathName;

    public Task<StoredRecording?> InspectAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (ThrowOnInspect is not null)
        {
            throw ThrowOnInspect;
        }

        return Task.FromResult(Stored);
    }

    public Task<long> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }

        Deleted.Add(storageKey);
        return Task.FromResult(Stored?.SizeBytes ?? 0L);
    }
}

public sealed class TestCorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = "test-correlation-id";
}

/// <summary>
/// In-memory egress relay. Stands in for the relay service the same way
/// <see cref="FakeMediaGateway"/> stands in for the media gateway: the process it supervises is
/// external, but everything the control plane decides about it is exercised for real.
/// </summary>
public sealed class FakeStreamRelay : IStreamRelay
{
    private readonly ConcurrentDictionary<Guid, RelayState> _relays = new();

    /// <summary>Every start request received, so tests can assert what the relay was actually told.</summary>
    public List<RelayStartRequest> StartRequests { get; } = [];

    public List<Guid> StopRequests { get; } = [];

    /// <summary>When set, <see cref="StartAsync"/> refuses with this reason.</summary>
    public string? RefuseStartReason { get; set; }

    /// <summary>When set, every call throws — simulating the relay service being unreachable.</summary>
    public Exception? ThrowOnAnyCall { get; set; }

    public Task<RelayStartResult> StartAsync(RelayStartRequest request, CancellationToken cancellationToken)
    {
        ThrowIfConfigured();
        StartRequests.Add(request);

        if (RefuseStartReason is { } reason)
        {
            return Task.FromResult(new RelayStartResult(false, null, reason));
        }

        var state = new RelayState(request.DestinationId, RelayPhase.Starting, DateTimeOffset.UtcNow, null,
            0, 0, null, null, null);

        _relays[request.DestinationId] = state;
        return Task.FromResult(new RelayStartResult(true, state));
    }

    public Task StopAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured();
        StopRequests.Add(destinationId);
        _relays.TryRemove(destinationId, out _);
        return Task.CompletedTask;
    }

    public Task<RelayState?> GetAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured();
        return Task.FromResult(_relays.GetValueOrDefault(destinationId));
    }

    public Task<IReadOnlyList<RelayState>> ListAsync(CancellationToken cancellationToken)
    {
        ThrowIfConfigured();
        return Task.FromResult<IReadOnlyList<RelayState>>(_relays.Values.ToList());
    }

    /// <summary>Simulates the platform accepting the stream.</summary>
    public void MarkConnected(Guid destinationId, long bytesSent = 4096)
    {
        var now = DateTimeOffset.UtcNow;
        _relays[destinationId] = new RelayState(destinationId, RelayPhase.Connected, now, now, bytesSent,
            0, null, null, null);
    }

    /// <summary>Simulates the relay process exiting with an error.</summary>
    public void MarkFailed(Guid destinationId, string errorCode, string message)
    {
        var existing = _relays.GetValueOrDefault(destinationId);
        _relays[destinationId] = new RelayState(destinationId, RelayPhase.Failed, existing?.StartedAt,
            existing?.ConnectedAt, existing?.BytesSent ?? 0, (existing?.RestartCount ?? 0) + 1,
            errorCode, message, DateTimeOffset.UtcNow);
    }

    /// <summary>Simulates the relay service losing all state, as it would across a restart.</summary>
    public void Forget(Guid destinationId) => _relays.TryRemove(destinationId, out _);

    private void ThrowIfConfigured()
    {
        if (ThrowOnAnyCall is not null)
        {
            throw ThrowOnAnyCall;
        }
    }
}

/// <summary>
/// Provider adapter under test control. Registered for a provider so destination behaviour can be
/// exercised without any external platform.
/// </summary>
public sealed class FakeDestinationAdapter(LiveStream.Domain.Distribution.DestinationProvider provider)
    : IDestinationProviderAdapter
{
    public LiveStream.Domain.Distribution.DestinationProvider Provider { get; } = provider;

    public string IngestUrl { get; set; } = "rtmp://fake.test/live";

    public string StreamKey { get; set; } = "fake-stream-key";

    /// <summary>When set, resolution fails with this outcome instead of succeeding.</summary>
    public DestinationTargetResolution? FailResolutionWith { get; set; }

    public int ResolveCallCount { get; private set; }

    public int LiveCallCount { get; private set; }

    public int EndedCallCount { get; private set; }

    public DestinationProviderDescriptor Describe() => new(
        Provider, Provider.ToString(), SupportsStreamKey: true, SupportsLinkedAccount: false,
        LinkedAccountConfigured: false, DefaultIngestUrl: IngestUrl, StreamKeyHelp: "test", HelpUrl: null);

    public Task<DestinationTargetResolution> ResolveTargetAsync(DestinationResolveContext context,
        CancellationToken cancellationToken)
    {
        ResolveCallCount++;

        return Task.FromResult(FailResolutionWith
            ?? new DestinationTargetResolution(true, IngestUrl, StreamKey, "broadcast-1",
                "https://example.test/watch"));
    }

    public Task<ProviderOperationResult> OnBroadcastLiveAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken)
    {
        LiveCallCount++;
        return Task.FromResult(ProviderOperationResult.Ok);
    }

    public Task<ProviderOperationResult> OnBroadcastEndedAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken)
    {
        EndedCallCount++;
        return Task.FromResult(ProviderOperationResult.Ok);
    }
}

/// <summary>
/// An <see cref="IAiAnalyst"/> that answers without a model.
///
/// The point of the AI provider seam is that everything above it can be exercised for real without
/// an API key and without spending money. This records exactly what it was asked — which is how the
/// tests check that no credential or identity reaches a prompt — and can be told to fail either
/// retryably or terminally, because that distinction drives the whole retry policy.
/// </summary>
public sealed class FakeAiAnalyst : IAiAnalyst
{
    /// <summary>Every request received, so a test can assert what would have been sent to a model.</summary>
    public List<AiAnalysisRequest> Requests { get; } = [];

    public bool IsConfigured { get; set; } = true;

    /// <summary>When set, every call throws it.</summary>
    public AiProviderException? ThrowOnCall { get; set; }

    /// <summary>
    /// Fails this many times before succeeding, for exercising the retry path.
    ///
    /// Setting it restarts the count. Without that, a failure spent on an unrelated job left over
    /// from an earlier test satisfies the quota, and the test that asked for a failure silently
    /// gets a success.
    /// </summary>
    public int FailuresBeforeSuccess
    {
        get => _failuresBeforeSuccess;
        set
        {
            _failuresBeforeSuccess = value;
            _failuresSoFar = 0;
        }
    }

    private int _failuresBeforeSuccess;

    public string ModelId { get; set; } = "claude-opus-5";

    public int InputTokens { get; set; } = 1200;

    public int OutputTokens { get; set; } = 350;

    private int _failuresSoFar;

    public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (ThrowOnCall is { } configured)
        {
            throw configured;
        }

        if (_failuresSoFar < FailuresBeforeSuccess)
        {
            _failuresSoFar += 1;
            throw new AiProviderException("AI_PROVIDER_UNAVAILABLE", "Temporarily down.", retryable: true);
        }

        var summary = $"Analysed {request.Timeline.Count} events for {request.Kind}.";

        return Task.FromResult(new AiAnalysisResult(
            summary,
            JsonSerializer.Serialize(new { summary, entries = request.Timeline.Count }),
            ModelId,
            InputTokens,
            OutputTokens));
    }
}

/// <summary>
/// Collects email instead of sending it, and can be told to fail.
///
/// The failure case is not decoration: a password reset whose mail could not be posted must still
/// behave correctly — the token stands, the caller is told nothing different, and nothing leaks
/// about whether the address existed.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    /// <summary>When true, every send reports failure the way a refused mail server does.</summary>
    public bool FailSends { get; set; }

    public EmailMessage? Last => Sent.Count > 0 ? Sent[^1] : null;

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.FromResult(!FailSends);
    }
}

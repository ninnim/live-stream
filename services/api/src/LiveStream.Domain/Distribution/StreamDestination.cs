using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Distribution;

/// <summary>
/// One external platform a Live Session is republished to.
///
/// Session-scoped, matching the destination contract in docs/06-multi-platform-distribution.md.
/// Status may only change through <see cref="TransitionTo"/>, which enforces
/// <see cref="DestinationStateMachine"/> and appends an audit event.
///
/// Nothing here can move the parent session. That isolation is the whole point: a destination is a
/// best-effort republish, and the broadcast must survive its failure.
/// </summary>
public class StreamDestination
{
    private readonly List<DestinationEvent> _events = [];

    private StreamDestination()
    {
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid LiveSessionId { get; set; }

    public DestinationProvider Provider { get; private set; }

    public string DisplayName { get; private set; }

    public DestinationCredentialMode CredentialMode { get; private set; }

    /// <summary>
    /// Operator-supplied RTMP/RTMPS ingest URL, for <see cref="DestinationCredentialMode.StreamKey"/>.
    /// Null for linked accounts, where the provider hands us a fresh URL per broadcast.
    /// </summary>
    public string? IngestUrl { get; private set; }

    /// <summary>
    /// Encrypted stream key. Never decrypted anywhere a response is built, and never logged.
    /// Null for linked accounts.
    /// </summary>
    public string? StreamKeyCipher { get; private set; }

    /// <summary>The linked account to mint a broadcast from, for <see cref="DestinationCredentialMode.LinkedAccount"/>.</summary>
    public Guid? ProviderAccountId { get; private set; }

    /// <summary>Excluded from the next start when false, without deleting the configuration.</summary>
    public bool Enabled { get; private set; }

    public DestinationStatus Status { get; private set; }

    /// <summary>Normalized internal error code. Provider-specific codes are mapped before they reach here.</summary>
    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    // --- Per-run state, cleared by ResetForRun ------------------------------------------------

    /// <summary>
    /// The RTMP URL the relay is actually publishing to for this run. For a linked account this is
    /// resolved from the provider at start; for a stream key it is <see cref="IngestUrl"/>.
    /// </summary>
    public string? ResolvedIngestUrl { get; private set; }

    /// <summary>Encrypted per-run stream key resolved from the provider. Erased when the run ends.</summary>
    public string? ResolvedStreamKeyCipher { get; private set; }

    /// <summary>Provider identifier for the broadcast created for this run, used to finalize it on stop.</summary>
    public string? ExternalBroadcastId { get; private set; }

    /// <summary>Public URL of the broadcast on the platform, safe to show to the operator.</summary>
    public string? WatchUrl { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextRetryAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? StoppedAt { get; private set; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    /// <summary>Bytes pushed to the platform across this run, including previous attempts.</summary>
    public long BytesSent { get; private set; }

    /// <summary>
    /// Total carried over from earlier attempts in this run.
    ///
    /// Each retry starts a fresh encoder whose own counter begins at zero, so the reported figure
    /// is added to this rather than compared with the running total. Without it, throughput appears
    /// frozen after every reconnect until the new attempt overtakes the old one.
    /// </summary>
    public long BytesSentBaseline { get; private set; }

    public DateTimeOffset StateEnteredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token; prevents a reconciler and an operator racing each other.</summary>
    public int Version { get; private set; }

    public LiveSession? LiveSession { get; set; }

    public ProviderAccount? ProviderAccount { get; set; }

    public IReadOnlyCollection<DestinationEvent> Events => _events;

    // -----------------------------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------------------------

    /// <summary>Creates a destination that publishes with an operator-supplied stream key.</summary>
    public static StreamDestination CreateWithStreamKey(
        Guid liveSessionId,
        DestinationProvider provider,
        string displayName,
        string ingestUrl,
        string streamKeyCipher,
        Guid actorUserId,
        DateTimeOffset now)
    {
        var destination = new StreamDestination
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            Provider = provider,
            DisplayName = ValidateDisplayName(displayName),
            CredentialMode = DestinationCredentialMode.StreamKey,
            IngestUrl = ValidateIngestUrl(ingestUrl),
            StreamKeyCipher = RequireCipher(streamKeyCipher),
            Enabled = true,
            Status = DestinationStatus.Idle,
            StateEnteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        destination.RecordEvent(DestinationEventType.Created, now, actorUserId,
            detail: $"provider={provider}; mode=StreamKey");

        return destination;
    }

    /// <summary>Creates a destination that mints a broadcast from an OAuth-linked account.</summary>
    public static StreamDestination CreateWithLinkedAccount(
        Guid liveSessionId,
        DestinationProvider provider,
        string displayName,
        Guid providerAccountId,
        Guid actorUserId,
        DateTimeOffset now)
    {
        var destination = new StreamDestination
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            Provider = provider,
            DisplayName = ValidateDisplayName(displayName),
            CredentialMode = DestinationCredentialMode.LinkedAccount,
            ProviderAccountId = providerAccountId,
            Enabled = true,
            Status = DestinationStatus.Idle,
            StateEnteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        destination.RecordEvent(DestinationEventType.Created, now, actorUserId,
            detail: $"provider={provider}; mode=LinkedAccount");

        return destination;
    }

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a validated state change. The only way <see cref="Status"/> can change.
    /// Repeating the current state is a no-op, so duplicate relay reports stay idempotent.
    /// </summary>
    public void TransitionTo(DestinationStatus target, DateTimeOffset now, Guid? actorUserId = null,
        string? reason = null, string? errorCode = null, string? correlationId = null)
    {
        if (Status == target)
        {
            return;
        }

        DestinationStateMachine.EnsureCanTransition(Status, target);

        var previous = Status;
        Status = target;
        StateEnteredAt = now;
        UpdatedAt = now;

        switch (target)
        {
            case DestinationStatus.Live:
                StartedAt ??= now;
                LastConnectedAt = now;
                LastErrorCode = null;
                LastErrorMessage = null;
                NextRetryAt = null;
                break;

            case DestinationStatus.Stopped or DestinationStatus.Error:
                StoppedAt = now;
                NextRetryAt = null;
                break;
        }

        if (errorCode is not null)
        {
            LastErrorCode = Truncate(errorCode, 64);
            LastErrorMessage = Truncate(reason, 500);
        }

        _events.Add(new DestinationEvent
        {
            StreamDestinationId = Id,
            Type = MapEventType(target),
            FromStatus = previous,
            ToStatus = target,
            ErrorCode = errorCode,
            Detail = reason,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        });
    }

    /// <summary>
    /// Clears everything belonging to the previous broadcast so a destination reused across sessions
    /// cannot leak a stale key, broadcast id, or retry count into the new run.
    /// </summary>
    public void ResetForRun(DateTimeOffset now)
    {
        ResolvedIngestUrl = null;
        ResolvedStreamKeyCipher = null;
        ExternalBroadcastId = null;
        WatchUrl = null;
        AttemptCount = 0;
        NextRetryAt = null;
        StartedAt = null;
        StoppedAt = null;
        LastConnectedAt = null;
        BytesSent = 0;
        BytesSentBaseline = 0;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = now;
    }

    /// <summary>Records the RTMP target resolved for this run.</summary>
    public void ApplyResolvedTarget(string ingestUrl, string streamKeyCipher, string? externalBroadcastId,
        string? watchUrl, DateTimeOffset now, string? correlationId = null)
    {
        ResolvedIngestUrl = ValidateIngestUrl(ingestUrl);
        ResolvedStreamKeyCipher = RequireCipher(streamKeyCipher);
        ExternalBroadcastId = Truncate(externalBroadcastId, 200);
        WatchUrl = Truncate(watchUrl, 1000);

        // A new attempt is about to start with its own zeroed counter, so bank what has been sent
        // so far. This is the point at which every attempt begins.
        BytesSentBaseline = BytesSent;
        UpdatedAt = now;

        // The URL is recorded, the key is not. Ingest URLs are public platform endpoints; the key is
        // the secret, and the two are only ever combined inside the relay.
        RecordEvent(DestinationEventType.TargetResolved, now,
            detail: $"ingest={RedactQuery(ResolvedIngestUrl)}", correlationId: correlationId);
    }

    /// <summary>Schedules the next reconnection attempt and moves the destination to RETRYING.</summary>
    public void ScheduleRetry(DateTimeOffset now, TimeSpan delay, string errorCode, string reason,
        string? correlationId = null)
    {
        AttemptCount++;
        TransitionTo(DestinationStatus.Retrying, now, reason: reason, errorCode: errorCode,
            correlationId: correlationId);
        NextRetryAt = now.Add(delay);

        RecordEvent(DestinationEventType.RetryScheduled, now,
            detail: $"attempt={AttemptCount}; nextRetryInSeconds={(int)delay.TotalSeconds}",
            errorCode: errorCode, correlationId: correlationId);
    }

    /// <summary>Gives up after the retry budget is spent, leaving the session untouched.</summary>
    public void ExhaustRetries(DateTimeOffset now, string errorCode, string reason, string? correlationId = null)
    {
        RecordEvent(DestinationEventType.RetryExhausted, now,
            detail: $"attempts={AttemptCount}", errorCode: errorCode, correlationId: correlationId);

        TransitionTo(DestinationStatus.Error, now, reason: reason, errorCode: errorCode,
            correlationId: correlationId);
    }

    public bool IsRetryDue(DateTimeOffset now) =>
        Status == DestinationStatus.Retrying && NextRetryAt is { } due && now >= due;

    /// <summary>Records what the current attempt has pushed, on top of everything before it.</summary>
    public void RecordBytesSent(long bytesSentThisAttempt, DateTimeOffset now)
    {
        if (bytesSentThisAttempt < 0)
        {
            return;
        }

        var total = BytesSentBaseline + bytesSentThisAttempt;

        // Never let the figure go backwards, whatever the relay reports.
        if (total <= BytesSent)
        {
            return;
        }

        BytesSent = total;
        UpdatedAt = now;
    }

    // -----------------------------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------------------------

    public void UpdateDisplayName(string displayName, DateTimeOffset now, Guid actorUserId)
    {
        DisplayName = ValidateDisplayName(displayName);
        UpdatedAt = now;
        RecordEvent(DestinationEventType.Updated, now, actorUserId, detail: "displayName changed");
    }

    public void UpdateStreamKey(string ingestUrl, string streamKeyCipher, DateTimeOffset now, Guid actorUserId)
    {
        if (CredentialMode != DestinationCredentialMode.StreamKey)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "This destination publishes through a linked account and has no stream key to change.");
        }

        IngestUrl = ValidateIngestUrl(ingestUrl);
        StreamKeyCipher = RequireCipher(streamKeyCipher);
        UpdatedAt = now;

        RecordEvent(DestinationEventType.CredentialRotated, now, actorUserId,
            detail: $"ingest={RedactQuery(IngestUrl)}");
    }

    public void SetEnabled(bool enabled, DateTimeOffset now, Guid actorUserId)
    {
        if (Enabled == enabled)
        {
            return;
        }

        if (!enabled && DestinationStateMachine.IsActive(Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "Stop this destination before disabling it.");
        }

        Enabled = enabled;
        UpdatedAt = now;

        // Disabled is a state, not just a flag, so the reconciler can skip the destination without
        // re-reading configuration on every pass.
        if (!enabled && Status is DestinationStatus.Idle or DestinationStatus.Stopped or DestinationStatus.Error)
        {
            TransitionTo(DestinationStatus.Disabled, now, actorUserId, reason: "Disabled by operator.");
        }
        else if (enabled && Status is DestinationStatus.Disabled)
        {
            TransitionTo(DestinationStatus.Idle, now, actorUserId, reason: "Enabled by operator.");
        }

        RecordEvent(enabled ? DestinationEventType.Enabled : DestinationEventType.Disabled, now, actorUserId);
    }

    public DestinationEvent RecordEvent(DestinationEventType type, DateTimeOffset now, Guid? actorUserId = null,
        string? detail = null, string? errorCode = null, string? correlationId = null)
    {
        var destinationEvent = new DestinationEvent
        {
            StreamDestinationId = Id,
            Type = type,
            FromStatus = Status,
            ToStatus = null,
            ErrorCode = errorCode,
            Detail = detail,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        };

        _events.Add(destinationEvent);
        return destinationEvent;
    }

    /// <summary>True when this destination should take part in the session that is starting.</summary>
    public bool IsStartable => Enabled && Status is DestinationStatus.Idle or DestinationStatus.Stopped
        or DestinationStatus.Error;

    // -----------------------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------------------

    private static string ValidateDisplayName(string? displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 120)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Destination name is required and must be 120 characters or fewer.");
        }

        return trimmed;
    }

    /// <summary>
    /// Accepts only RTMP/RTMPS URLs. This is a security boundary, not a formatting nicety: the value
    /// is handed to a relay process, so permitting an arbitrary scheme would let an operator point
    /// the encoder at a file path or an internal service.
    /// </summary>
    private static string ValidateIngestUrl(string? ingestUrl)
    {
        var trimmed = (ingestUrl ?? string.Empty).Trim();

        if (trimmed.Length is 0 or > 1000)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Ingest URL is required and must be 1000 characters or fewer.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Ingest URL must be an absolute URL.");
        }

        if (uri.Scheme is not ("rtmp" or "rtmps"))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Ingest URL must use rtmp:// or rtmps://.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Ingest URL must include a host.");
        }

        return trimmed;
    }

    private static string RequireCipher(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Stream key is required.");
        }

        return cipher;
    }

    /// <summary>Drops any query string before an ingest URL is written to an event or a log.</summary>
    private static string RedactQuery(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var queryStart = url.IndexOf('?');
        return queryStart < 0 ? url : url[..queryStart] + "?<redacted>";
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static DestinationEventType MapEventType(DestinationStatus target) => target switch
    {
        DestinationStatus.Live => DestinationEventType.Connected,
        DestinationStatus.Retrying => DestinationEventType.Disconnected,
        DestinationStatus.Error => DestinationEventType.Disconnected,
        DestinationStatus.Disabled => DestinationEventType.Disabled,
        DestinationStatus.Idle => DestinationEventType.Enabled,
        _ => DestinationEventType.StateChanged,
    };
}

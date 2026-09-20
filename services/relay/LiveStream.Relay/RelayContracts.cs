using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LiveStream.Relay;

/// <summary>
/// Instruction from the control plane to republish one session to one external endpoint.
///
/// <see cref="TargetStreamKey"/> arrives separately from <see cref="TargetUrl"/> and the two are
/// only ever joined inside the encoder command line, so neither this service nor the control plane
/// can accidentally log a complete, usable publishing URL.
/// </summary>
public sealed record StartRelayRequest
{
    [JsonPropertyName("destinationId")]
    public Guid DestinationId { get; init; }

    [JsonPropertyName("liveSessionId")]
    public Guid LiveSessionId { get; init; }

    [Required]
    [JsonPropertyName("mediaPathName")]
    public string MediaPathName { get; init; } = string.Empty;

    [Required]
    [JsonPropertyName("sourceCredential")]
    public string SourceCredential { get; init; } = string.Empty;

    [Required]
    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; init; } = string.Empty;

    [Required]
    [JsonPropertyName("targetStreamKey")]
    public string TargetStreamKey { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "CustomRtmp";
}

public sealed record RelayStateResponse
{
    [JsonPropertyName("destinationId")]
    public Guid DestinationId { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = nameof(RelayPhase.Starting);

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("connectedAt")]
    public DateTimeOffset? ConnectedAt { get; init; }

    [JsonPropertyName("bytesSent")]
    public long BytesSent { get; init; }

    [JsonPropertyName("restartCount")]
    public int RestartCount { get; init; }

    [JsonPropertyName("lastErrorCode")]
    public string? LastErrorCode { get; init; }

    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; init; }

    [JsonPropertyName("lastErrorAt")]
    public DateTimeOffset? LastErrorAt { get; init; }
}

public enum RelayPhase
{
    Starting = 0,
    Connected = 1,
    Failed = 2,
    Stopped = 3,
}

/// <summary>
/// Error codes shared with the control plane. Duplicated as constants rather than referenced,
/// because the relay deliberately takes no dependency on the API assemblies — it is a media-plane
/// process that happens to be written in the same language.
/// </summary>
public static class RelayErrorCodes
{
    /// <summary>The platform refused the connection or the key. Retrying will not help.</summary>
    public const string DestinationRejected = "LIVE_016_DESTINATION_REJECTED";

    /// <summary>The platform was unreachable or dropped the connection. Worth retrying.</summary>
    public const string DestinationUnavailable = "LIVE_017_DESTINATION_UNAVAILABLE";

    /// <summary>The encoder could not be started at all.</summary>
    public const string RelayUnavailable = "LIVE_018_RELAY_UNAVAILABLE";
}

using LiveStream.Domain.Sources;

namespace LiveStream.Application.Sources.Contracts;

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Invites a device. The role is chosen here by the operator, never by whoever redeems the code —
/// that binding is what stops a borrowed phone from claiming operator rights.
/// </summary>
public sealed record InviteSourceRequest(string Role, string DisplayName);

/// <summary>
/// Redeems a pairing code. Anonymous by necessity: the device has no account yet.
/// <see cref="DeviceLabel"/> lets the phone name itself, which is far more useful in a control room
/// than "Camera 2".
/// </summary>
public sealed record ClaimPairingRequest(string Code, string? DeviceLabel);

public sealed record RenameSourceRequest(string DisplayName);

// ---------------------------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A source as the control room sees it.
///
/// Carries no pairing code and no device token: both are returned exactly once, at the moment they
/// are minted, and are stored only as hashes.
/// </summary>
public sealed record SourceResponse(
    Guid Id,
    Guid LiveSessionId,
    string Role,
    string DisplayName,
    string Status,
    bool IsProgram,
    bool ContributesMedia,
    bool IngestConnected,
    int? BitrateKbps,
    long BytesReceived,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? PairedAt,
    DateTimeOffset? RevokedAt,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> AllowedTransitions,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>
/// Returned once when a device is invited.
///
/// <see cref="PairingCode"/> is the only time the plaintext exists outside the operator's screen.
/// <see cref="JoinUrl"/> is the same code in a form a QR reader can open directly.
/// </summary>
public sealed record SourceInvitationResponse(
    SourceResponse Source,
    string PairingCode,
    string JoinUrl,
    DateTimeOffset ExpiresAt,
    int ExpiresInSeconds);

/// <summary>
/// Returned once when a device redeems a code.
///
/// <see cref="DeviceToken"/> is the credential the device authenticates with from then on. It is
/// scoped to this one source, expires, and dies the instant the source is revoked.
/// </summary>
public sealed record DevicePairedResponse(
    string DeviceToken,
    DateTimeOffset ExpiresAt,
    Guid LiveSessionId,
    string SessionTitle,
    SourceResponse Source);

/// <summary>What a paired device is allowed to know about the session it joined.</summary>
public sealed record DeviceSessionResponse(
    Guid LiveSessionId,
    string SessionTitle,
    string SessionStatus,
    bool IsBroadcasting,
    SourceResponse Source,
    IReadOnlyList<string> Permissions);

/// <summary>
/// A short-lived way for the control room to watch one source.
///
/// The token is required for a private session, where path visibility alone would deny the read.
/// </summary>
public sealed record SourcePreviewResponse(
    Guid SourceId,
    string WebRtcUrl,
    string HlsUrl,
    string ReadToken,
    DateTimeOffset ExpiresAt);

public sealed record SourceEventResponse(
    Guid Id,
    string Type,
    string? FromStatus,
    string? ToStatus,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CreatedAt);

/// <summary>Body of the <c>sourceStateChanged</c> realtime event.</summary>
public sealed record SourceStatusResponse(
    Guid Id,
    Guid LiveSessionId,
    string Role,
    string DisplayName,
    string Status,
    bool IsProgram,
    bool IngestConnected,
    int? BitrateKbps,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset ObservedAt);

// ---------------------------------------------------------------------------------------------
// Mapping
// ---------------------------------------------------------------------------------------------

public static class SourceMapper
{
    public static SourceResponse ToResponse(SessionSource source) => new(
        source.Id,
        source.LiveSessionId,
        source.Role.ToString(),
        source.DisplayName,
        source.Status.ToString(),
        source.IsProgram,
        SourcePermissions.ContributesMedia(source.Role),
        source.IngestConnected,
        source.BitrateKbps,
        source.BytesReceived,
        source.LastSeenAt,
        source.PairedAt,
        source.RevokedAt,
        SourcePermissions.For(source.Role).Select(p => p.ToString()).ToList(),
        SourceStateMachine.AllowedTargets(source.Status).Select(s => s.ToString()).ToList(),
        source.UpdatedAt,
        source.Version);

    public static SourceStatusResponse ToStatusResponse(SessionSource source, DateTimeOffset now) => new(
        source.Id,
        source.LiveSessionId,
        source.Role.ToString(),
        source.DisplayName,
        source.Status.ToString(),
        source.IsProgram,
        source.IngestConnected,
        source.BitrateKbps,
        source.LastSeenAt,
        now);

    public static SourceEventResponse ToResponse(SourceEvent sourceEvent) => new(
        sourceEvent.Id,
        sourceEvent.Type.ToString(),
        sourceEvent.FromStatus?.ToString(),
        sourceEvent.ToStatus?.ToString(),
        sourceEvent.ErrorCode,
        sourceEvent.Detail,
        sourceEvent.CreatedAt);
}

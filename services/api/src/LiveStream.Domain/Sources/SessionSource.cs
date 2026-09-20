using System.Security.Cryptography;
using System.Text;
using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Sources;

/// <summary>
/// One device or participant contributing to a Live Session (docs/05-multi-device.md).
///
/// Holds two secrets, neither of which is ever stored in the clear or returned twice: the pairing
/// code a device redeems, and the device token it authenticates with afterwards. Both are hashed,
/// both expire, and revoking the source invalidates both immediately — which is what
/// "Device credentials are not permanent API keys" actually requires.
/// </summary>
public class SessionSource
{
    /// <summary>
    /// Alphabet for pairing codes: digits and capitals with 0/O/1/I/L/U removed.
    ///
    /// Someone is reading this off a screen and typing it into a phone, so characters that are
    /// routinely confused for each other cost more in mistyped codes than they add in entropy.
    /// </summary>
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int CodeLength = 8;

    private readonly List<SourceEvent> _events = [];

    private SessionSource()
    {
        DisplayName = string.Empty;
        MediaPathName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid LiveSessionId { get; set; }

    public SourceRole Role { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>
    /// This source's own unguessable ingest path. Separate per source: two devices cannot publish
    /// into one path, and a per-source path is what lets one be revoked without touching the others.
    /// Empty for roles that contribute no media.
    /// </summary>
    public string MediaPathName { get; private set; }

    public SourceStatus Status { get; private set; }

    /// <summary>SHA-256 of the pairing code. Cleared once redeemed, so a code is genuinely single-use.</summary>
    public string? PairingCodeHash { get; private set; }

    public DateTimeOffset? PairingCodeExpiresAt { get; private set; }

    /// <summary>SHA-256 of the device token issued on pairing. The plaintext is returned exactly once.</summary>
    public string? DeviceTokenHash { get; private set; }

    public DateTimeOffset? DeviceTokenExpiresAt { get; private set; }

    /// <summary>True when this source is the one viewers are currently watching.</summary>
    public bool IsProgram { get; private set; }

    // --- Presence and health, driven by the reconciler ----------------------------------------

    public bool IngestConnected { get; private set; }

    public int? BitrateKbps { get; private set; }

    public long BytesReceived { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public DateTimeOffset? PairedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset StateEnteredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token; the reconciler and an operator can race this row.</summary>
    public int Version { get; private set; }

    public LiveSession? LiveSession { get; set; }

    public IReadOnlyCollection<SourceEvent> Events => _events;

    // -----------------------------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the session's own studio source. Paired from the outset — the creator is already
    /// authenticated as a workspace user, so there is nothing for them to redeem.
    /// </summary>
    public static SessionSource CreateHost(Guid liveSessionId, string mediaPathName, Guid createdByUserId,
        DateTimeOffset now)
    {
        var source = new SessionSource
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            Role = SourceRole.Host,
            DisplayName = "Studio",
            MediaPathName = mediaPathName,
            Status = SourceStatus.Paired,
            IsProgram = true,
            CreatedByUserId = createdByUserId,
            PairedAt = now,
            StateEnteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        source.RecordEvent(SourceEventType.Paired, now, createdByUserId, detail: "role=Host");
        return source;
    }

    /// <summary>
    /// Invites a device by minting a pairing code. Returns the source together with the plaintext
    /// code, which the caller must show once and then forget.
    /// </summary>
    public static (SessionSource Source, string PairingCode) Invite(
        Guid liveSessionId,
        SourceRole role,
        string displayName,
        string mediaPathName,
        Guid createdByUserId,
        DateTimeOffset now,
        TimeSpan codeLifetime)
    {
        if (role is SourceRole.Host)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "A session already has its host source; invite a camera, screen, audio, moderator or operator.");
        }

        if (codeLifetime <= TimeSpan.Zero)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Pairing code lifetime must be positive.");
        }

        var code = GenerateCode();

        var source = new SessionSource
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            Role = role,
            DisplayName = ValidateDisplayName(displayName),
            // Roles that send no media get no ingest path at all, rather than an unused one.
            MediaPathName = SourcePermissions.ContributesMedia(role) ? mediaPathName : string.Empty,
            Status = SourceStatus.Invited,
            PairingCodeHash = HashSecret(code),
            PairingCodeExpiresAt = now.Add(codeLifetime),
            CreatedByUserId = createdByUserId,
            StateEnteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        source.RecordEvent(SourceEventType.Invited, now, createdByUserId,
            detail: $"role={role}; expiresInSeconds={(int)codeLifetime.TotalSeconds}");

        return (source, code);
    }

    // -----------------------------------------------------------------------------------------
    // Pairing
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Redeems the pairing code and issues a device token. Returns the plaintext token, which is
    /// returned to the device exactly once.
    /// </summary>
    public string Claim(DateTimeOffset now, TimeSpan tokenLifetime, string? deviceLabel = null)
    {
        if (Status is not SourceStatus.Invited)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "This pairing code has already been used.");
        }

        if (PairingCodeExpiresAt is not { } expiry || now >= expiry)
        {
            throw new DomainException(ErrorCodes.CredentialExpired,
                "This pairing code has expired. Ask for a new one.");
        }

        var token = GenerateToken();

        // Cleared, not merely marked used: a redeemed code must not be redeemable a second time
        // even if the status check above is ever bypassed.
        PairingCodeHash = null;
        PairingCodeExpiresAt = null;

        DeviceTokenHash = HashSecret(token);
        DeviceTokenExpiresAt = now.Add(tokenLifetime);
        PairedAt = now;

        // The operator's name stands. A device describing itself is useful for diagnostics, but
        // letting it overwrite the name an operator deliberately chose means a control room labelled
        // "Stage left camera" silently becomes "Windows PC" the moment someone joins.
        var reason = string.IsNullOrWhiteSpace(deviceLabel)
            ? "Device paired."
            : $"Device paired: {Truncate(deviceLabel, 80)}";

        TransitionTo(SourceStatus.Paired, now, reason: reason);

        return token;
    }

    /// <summary>True when this token can still be used. Revocation makes it false immediately.</summary>
    public bool IsDeviceTokenUsableAt(DateTimeOffset now) =>
        Status is not SourceStatus.Revoked
        && DeviceTokenHash is not null
        && DeviceTokenExpiresAt is { } expiry
        && now < expiry;

    public bool MatchesDeviceToken(string presentedToken) =>
        DeviceTokenHash is { } hash
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(HashSecret(presentedToken)));

    public bool MatchesPairingCode(string presentedCode) =>
        PairingCodeHash is { } hash
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(HashSecret(NormalizeCode(presentedCode))));

    /// <summary>
    /// Withdraws access immediately and irreversibly. Both secrets are erased rather than flagged,
    /// so nothing usable is left behind even in the stored row.
    /// </summary>
    public void Revoke(DateTimeOffset now, Guid? actorUserId, string reason)
    {
        if (Status is SourceStatus.Revoked)
        {
            return;
        }

        PairingCodeHash = null;
        PairingCodeExpiresAt = null;
        DeviceTokenHash = null;
        DeviceTokenExpiresAt = null;
        IngestConnected = false;
        IsProgram = false;
        RevokedAt = now;
        RevokedByUserId = actorUserId;

        TransitionTo(SourceStatus.Revoked, now, actorUserId, reason);
    }

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a validated state change. The only way <see cref="Status"/> can change.
    /// Repeating the current state is a no-op, so duplicate reconciler observations stay idempotent.
    /// </summary>
    public void TransitionTo(SourceStatus target, DateTimeOffset now, Guid? actorUserId = null,
        string? reason = null, string? correlationId = null)
    {
        if (Status == target)
        {
            return;
        }

        SourceStateMachine.EnsureCanTransition(Status, target);

        var previous = Status;
        Status = target;
        StateEnteredAt = now;
        UpdatedAt = now;

        _events.Add(new SourceEvent
        {
            SessionSourceId = Id,
            Type = MapEventType(target),
            FromStatus = previous,
            ToStatus = target,
            Detail = reason,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        });
    }

    /// <summary>Records one media-plane observation. Called by the reconciler, never by a client.</summary>
    public void ObserveMedia(bool ingestConnected, int? bitrateKbps, long bytesReceived, DateTimeOffset now)
    {
        IngestConnected = ingestConnected;
        BitrateKbps = ingestConnected ? bitrateKbps : null;

        if (bytesReceived > BytesReceived)
        {
            BytesReceived = bytesReceived;
        }

        if (ingestConnected)
        {
            LastSeenAt = now;
        }

        UpdatedAt = now;

        if (ingestConnected && Status is SourceStatus.Paired or SourceStatus.Disconnected)
        {
            TransitionTo(SourceStatus.Connected, now, reason: "Media detected.");
        }
        else if (!ingestConnected && Status is SourceStatus.Connected)
        {
            TransitionTo(SourceStatus.Disconnected, now, reason: "Media stopped.");
        }
    }

    public void Rename(string displayName, DateTimeOffset now, Guid actorUserId)
    {
        DisplayName = ValidateDisplayName(displayName);
        UpdatedAt = now;
        RecordEvent(SourceEventType.Renamed, now, actorUserId);
    }

    /// <summary>
    /// Marks this source as the program feed. The caller is responsible for clearing the flag on
    /// whichever source held it — a session has exactly one program at a time.
    /// </summary>
    public void PromoteToProgram(DateTimeOffset now, Guid? actorUserId, string? correlationId = null)
    {
        if (!SourcePermissions.ContributesMedia(Role))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"A {Role} source sends no media and cannot be put on air.");
        }

        if (Status is not SourceStatus.Connected)
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "This source is not sending media yet.");
        }

        if (IsProgram)
        {
            return;
        }

        IsProgram = true;
        UpdatedAt = now;
        RecordEvent(SourceEventType.PromotedToProgram, now, actorUserId, correlationId: correlationId);
    }

    public void RemoveFromProgram(DateTimeOffset now, Guid? actorUserId, string? correlationId = null)
    {
        if (!IsProgram)
        {
            return;
        }

        IsProgram = false;
        UpdatedAt = now;
        RecordEvent(SourceEventType.RemovedFromProgram, now, actorUserId, correlationId: correlationId);
    }

    public SourceEvent RecordEvent(SourceEventType type, DateTimeOffset now, Guid? actorUserId = null,
        string? detail = null, string? errorCode = null, string? correlationId = null)
    {
        var sourceEvent = new SourceEvent
        {
            SessionSourceId = Id,
            Type = type,
            FromStatus = Status,
            ToStatus = null,
            ErrorCode = errorCode,
            Detail = detail,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        };

        _events.Add(sourceEvent);
        return sourceEvent;
    }

    public bool Allows(SourcePermission permission) => SourcePermissions.Allows(Role, permission);

    // -----------------------------------------------------------------------------------------
    // Secrets
    // -----------------------------------------------------------------------------------------

    /// <summary>Formats a code for display as <c>ABCD-EFGH</c>. Purely cosmetic; input is normalized.</summary>
    public static string FormatCode(string code) =>
        code.Length == CodeLength ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>
    /// Strips separators and upper-cases, so a code typed as "abcd efgh" or "ABCD-EFGH" matches.
    /// </summary>
    public static string NormalizeCode(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(CodeLength);

        foreach (var character in presented)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static string HashSecret(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>
    /// A code short enough to type on a phone.
    ///
    /// Eight characters from a 30-character alphabet is roughly 39 bits — far less than a token,
    /// which is why codes expire in minutes, are single-use, and sit behind a rate limit. Those
    /// three properties, not the length, are what make guessing impractical.
    /// </summary>
    private static string GenerateCode()
    {
        var builder = new StringBuilder(CodeLength);

        for (var i = 0; i < CodeLength; i++)
        {
            builder.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>256 bits, URL-safe. This is the credential the device actually authenticates with.</summary>
    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string ValidateDisplayName(string? displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();

        if (trimmed.Length is 0 or > 80)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Source name is required and must be 80 characters or fewer.");
        }

        return trimmed;
    }

    private static SourceEventType MapEventType(SourceStatus target) => target switch
    {
        SourceStatus.Paired => SourceEventType.Paired,
        SourceStatus.Connected => SourceEventType.Connected,
        SourceStatus.Disconnected => SourceEventType.Disconnected,
        SourceStatus.Revoked => SourceEventType.Revoked,
        _ => SourceEventType.Invited,
    };
}

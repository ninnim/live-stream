using System.Security.Cryptography;
using System.Text;
using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Media;

/// <summary>Operations a scoped media credential permits.</summary>
public enum IngestCredentialScope
{
    /// <summary>Publish media into the session ingest path.</summary>
    Publish = 0,

    /// <summary>Read media from the session path (private/unlisted playback).</summary>
    Read = 1,
}

/// <summary>
/// A short-lived, scoped credential that lets one browser publish into one session path.
/// The plaintext secret is returned to the caller exactly once and never persisted:
/// only a SHA-256 hash is stored (docs/11-security.md).
/// </summary>
public class IngestCredential
{
    private IngestCredential()
    {
        TokenHash = string.Empty;
        MediaPathName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid LiveSessionId { get; set; }

    public Guid IssuedToUserId { get; private set; }

    /// <summary>SHA-256 of the plaintext token, hex encoded. The plaintext is never stored.</summary>
    public string TokenHash { get; private set; }

    /// <summary>The single media path this credential is valid for. Prevents cross-session reuse.</summary>
    public string MediaPathName { get; private set; }

    public IngestCredentialScope Scope { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? FirstUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public LiveSession? LiveSession { get; set; }

    /// <summary>
    /// Mints a credential and returns it together with the plaintext token. The caller must hand the
    /// plaintext straight to the requesting client and then forget it.
    /// </summary>
    public static (IngestCredential Credential, string PlaintextToken) Issue(
        Guid liveSessionId,
        Guid issuedToUserId,
        string mediaPathName,
        IngestCredentialScope scope,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Credential lifetime must be positive.");
        }

        var plaintext = GenerateToken();
        var credential = new IngestCredential
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            IssuedToUserId = issuedToUserId,
            MediaPathName = mediaPathName,
            Scope = scope,
            TokenHash = HashToken(plaintext),
            IssuedAt = now,
            ExpiresAt = now.Add(lifetime),
        };

        return (credential, plaintext);
    }

    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void MarkUsed(DateTimeOffset now) => FirstUsedAt ??= now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public static string HashToken(string plaintextToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

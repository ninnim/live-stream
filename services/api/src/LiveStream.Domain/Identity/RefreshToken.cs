using System.Security.Cryptography;
using System.Text;

namespace LiveStream.Domain.Identity;

/// <summary>
/// A rotating refresh token. Only the hash is stored; presenting an already-rotated token revokes
/// the whole chain, which is the standard detection for stolen-token replay (docs/11-security.md).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when this token was exchanged, pointing at its successor.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    public User? User { get; set; }

    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static string Hash(string plaintextToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));

    public static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

using System.Security.Cryptography;
using System.Text;

namespace LiveStream.Domain.Identity;

/// <summary>
/// A single-use token that lets somebody who has lost their password set a new one.
///
/// Only the hash is stored, exactly as for <see cref="RefreshToken"/>. The plaintext exists for the
/// few milliseconds it takes to put it in an email and is never written anywhere — so a stolen
/// database backup cannot be used to reset anybody's password, which is the whole reason this is
/// not just a column on the user.
///
/// It is deliberately short-lived and single-use. A reset link is the one credential that arrives
/// over email, sits in an inbox indefinitely, and is forwarded by people who do not think of it as
/// a credential at all.
/// </summary>
public class PasswordResetToken
{
    /// <summary>
    /// One hour.
    ///
    /// Long enough to find the email, notice it, and act on it — including the case where it took
    /// a few minutes to arrive. Short enough that a link left in an inbox stops being a way in
    /// before the day is out.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set the moment it is redeemed. What makes the token single-use.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>
    /// Set when a newer request supersedes this one, or when the password changes by another route.
    /// Distinct from <see cref="UsedAt"/> so an audit can tell "somebody reset their password" from
    /// "somebody asked twice".
    /// </summary>
    public DateTimeOffset? InvalidatedAt { get; set; }

    public User? User { get; set; }

    public bool IsUsableAt(DateTimeOffset now) =>
        UsedAt is null && InvalidatedAt is null && now < ExpiresAt;

    /// <summary>
    /// SHA-256 rather than a password hash, and that is deliberate.
    ///
    /// A slow hash exists to make guessing a low-entropy human-chosen secret expensive. This token
    /// is 256 bits from a cryptographic generator, so there is nothing to guess, and a slow hash
    /// would only make the lookup below expensive for us.
    /// </summary>
    public static string Hash(string plaintextToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));

    public static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);

        // URL-safe: this goes into a link, and a '+' in a query string is a space.
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

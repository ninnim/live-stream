using System.Security.Cryptography;
using System.Text;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure.Security;

/// <summary>
/// AES-256-GCM protection for stored secrets.
///
/// GCM rather than CBC because it authenticates as well as encrypts: a tampered ciphertext fails to
/// decrypt instead of yielding attacker-influenced plaintext. That matters here — the plaintext is
/// fed to a process as an RTMP target, so a malleable ciphertext would be a redirection primitive.
///
/// Format: <c>v1:{keyId}:{base64(nonce | ciphertext | tag)}</c>. The key id travels with the value
/// so keys can be rotated by adding a new primary and leaving the old one configured for reads.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly string _primaryKeyId;

    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        var value = options.Value;

        _keys = value.Keys.ToDictionary(
            pair => pair.Key,
            pair => Convert.FromBase64String(pair.Value),
            StringComparer.Ordinal);

        _primaryKeyId = value.PrimaryKeyId;

        if (!_keys.ContainsKey(_primaryKeyId))
        {
            // Options validation catches this at startup; this guards direct construction in tests.
            throw new InvalidOperationException(
                $"Secret protection primary key '{_primaryKeyId}' is not configured.");
        }
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = _keys[_primaryKeyId];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Nonce, ciphertext and tag are stored as one blob so a row can never hold a mismatched set.
        var output = new byte[NonceSize + plaintextBytes.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        var ciphertext = output.AsSpan(NonceSize, plaintextBytes.Length);
        var tag = output.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

        // A random nonce per encryption is mandatory for GCM: reusing one with the same key leaks
        // the plaintext relationship and can expose the authentication subkey.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        CryptographicOperations.ZeroMemory(plaintextBytes);

        return $"{Version}:{_primaryKeyId}:{Convert.ToBase64String(output)}";
    }

    public string Unprotect(string protectedValue)
    {
        if (!TryParse(protectedValue, out var keyId, out var payload))
        {
            throw new DomainException(ErrorCodes.SecretProtectionFailed,
                "A stored credential could not be read.");
        }

        if (!_keys.TryGetValue(keyId, out var key))
        {
            // The usual cause is a key removed from configuration while rows still reference it.
            throw new DomainException(ErrorCodes.SecretProtectionFailed,
                $"A stored credential was encrypted with key '{keyId}', which is no longer configured.");
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new DomainException(ErrorCodes.SecretProtectionFailed,
                "A stored credential could not be read.");
        }

        var plaintextLength = payload.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLength];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                payload.AsSpan(0, NonceSize),
                payload.AsSpan(NonceSize, plaintextLength),
                payload.AsSpan(NonceSize + plaintextLength, TagSize),
                plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // Authentication failure: wrong key, or the ciphertext was altered. Both are the same
            // answer to the caller, and neither reveals which.
            throw new DomainException(ErrorCodes.SecretProtectionFailed,
                "A stored credential could not be read.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool NeedsReprotection(string protectedValue) =>
        !TryParse(protectedValue, out var keyId, out _) || !string.Equals(keyId, _primaryKeyId, StringComparison.Ordinal);

    private static bool TryParse(string? value, out string keyId, out byte[] payload)
    {
        keyId = string.Empty;
        payload = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Exactly three parts: splitting with a limit keeps a ':' inside base64 from being possible
        // to exploit, though base64 never contains one.
        var parts = value.Split(':', 3);
        if (parts.Length != 3 || parts[0] != Version || parts[1].Length == 0)
        {
            return false;
        }

        try
        {
            payload = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        keyId = parts[1];
        return true;
    }
}

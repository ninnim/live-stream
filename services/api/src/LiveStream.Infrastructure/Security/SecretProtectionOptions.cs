using System.ComponentModel.DataAnnotations;

namespace LiveStream.Infrastructure.Security;

/// <summary>
/// Keys used to encrypt stream keys and OAuth tokens at rest.
///
/// Supplied through environment variables or a secret store, never through committed configuration
/// (docs/11-security.md, MASTER_BLUEPRINT.md §31 "Use a secret manager when production scale
/// requires it").
///
/// Multiple keys may be configured at once so a key can be rotated without a migration: the primary
/// key encrypts, and every configured key can decrypt.
/// </summary>
public sealed class SecretProtectionOptions : IValidatableObject
{
    public const string SectionName = "Secrets";

    /// <summary>Identifier of the key new ciphertext is produced with. Must be present in <see cref="Keys"/>.</summary>
    [Required]
    public string PrimaryKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Key id to base64-encoded 256-bit key. Old keys stay here until every row that used them has
    /// been re-encrypted; removing one early makes those rows permanently unreadable.
    /// </summary>
    [Required]
    [MinLength(1)]
    public Dictionary<string, string> Keys { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Keys.ContainsKey(PrimaryKeyId))
        {
            yield return new ValidationResult(
                $"Secrets:PrimaryKeyId '{PrimaryKeyId}' has no matching entry in Secrets:Keys.",
                [nameof(PrimaryKeyId)]);
        }

        foreach (var (keyId, value) in Keys)
        {
            // A key id travels in the ciphertext, so it must survive the delimiter used there.
            if (keyId.Contains(':') || string.IsNullOrWhiteSpace(keyId))
            {
                yield return new ValidationResult(
                    $"Secrets:Keys key id '{keyId}' must be non-empty and must not contain ':'.", [nameof(Keys)]);
                continue;
            }

            if (!TryDecodeKey(value, out var length))
            {
                yield return new ValidationResult(
                    $"Secrets:Keys:{keyId} must be valid base64.", [nameof(Keys)]);
                continue;
            }

            if (length != 32)
            {
                yield return new ValidationResult(
                    $"Secrets:Keys:{keyId} must decode to 32 bytes (256 bits); got {length}.", [nameof(Keys)]);
            }
        }
    }

    private static bool TryDecodeKey(string value, out int length)
    {
        length = 0;
        Span<byte> buffer = stackalloc byte[64];

        if (!Convert.TryFromBase64String(value ?? string.Empty, buffer, out var written))
        {
            return false;
        }

        length = written;
        return true;
    }
}

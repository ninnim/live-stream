namespace LiveStream.Application.Abstractions;

/// <summary>
/// Reversible protection for secrets the platform must be able to use again — stream keys and
/// OAuth tokens (docs/11-security.md, MASTER_BLUEPRINT.md §31 "Secrets").
///
/// Deliberately distinct from password hashing. A password is verified, so it is hashed one-way and
/// never recovered. A stream key must be handed to an encoder verbatim months after it was entered,
/// so it is encrypted and decrypted. Conflating the two is how stream keys end up in plaintext
/// columns.
///
/// Ciphertext carries the identifier of the key that produced it, so keys can be rotated without
/// rewriting every stored row.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a secret with the current primary key. Returns opaque, storable text.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a value produced by <see cref="Protect"/>.
    /// Throws when the ciphertext is malformed, was produced by a key that is no longer configured,
    /// or fails its authentication tag — never returns a partial or garbled result.
    /// </summary>
    string Unprotect(string protectedValue);

    /// <summary>True when the value was produced by a key that is no longer the primary one.</summary>
    bool NeedsReprotection(string protectedValue);
}

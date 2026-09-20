using System.Security.Cryptography;

namespace LiveStream.Domain.Common;

/// <summary>Opaque, URL-safe identifiers for values that appear in media paths and URLs.</summary>
public static class IdGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Generates an unguessable media path segment (e.g. <c>ls_k3f9…</c>). Used instead of the
    /// database identifier so playback URLs cannot be enumerated and paths can be rotated.
    /// </summary>
    public static string NewMediaPathName() => "ls_" + RandomString(22);

    public static string RandomString(int length)
    {
        var buffer = new char[length];
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < length; i++)
        {
            buffer[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(buffer);
    }
}

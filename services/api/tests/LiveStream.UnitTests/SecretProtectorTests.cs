using System.Security.Cryptography;
using LiveStream.Domain.Common;
using LiveStream.Infrastructure.Distribution.Adapters;
using LiveStream.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Stream keys and OAuth tokens are the only long-lived third-party secrets the platform stores, so
/// the protector gets the same scrutiny as the state machine.
/// </summary>
public class SecretProtectorTests
{
    private const string PrimaryKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string SecondKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private static AesGcmSecretProtector Create(string primaryKeyId = "k1",
        Dictionary<string, string>? keys = null) =>
        new(Options.Create(new SecretProtectionOptions
        {
            PrimaryKeyId = primaryKeyId,
            Keys = keys ?? new Dictionary<string, string> { ["k1"] = PrimaryKey },
        }));

    [Theory]
    [InlineData("live_abcd-1234")]
    [InlineData("")]
    [InlineData("a key with spaces and symbols !@#$%^&*()")]
    [InlineData("unicode ☃ ünïcödé 日本語")]
    public void Round_trips_a_secret(string plaintext)
    {
        var protector = Create();
        var protectedValue = protector.Protect(plaintext);

        Assert.Equal(plaintext, protector.Unprotect(protectedValue));
    }

    /// <summary>The stored form must not contain the plaintext, which is the entire point.</summary>
    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var protector = Create();
        var protectedValue = protector.Protect("super-secret-stream-key");

        Assert.DoesNotContain("super-secret-stream-key", protectedValue);
    }

    /// <summary>
    /// A random nonce per encryption means the same input never produces the same output. Without
    /// it, an observer of the database could tell which destinations share a key.
    /// </summary>
    [Fact]
    public void Encrypting_the_same_value_twice_produces_different_ciphertext()
    {
        var protector = Create();

        var first = protector.Protect("same-value");
        var second = protector.Protect("same-value");

        Assert.NotEqual(first, second);
        Assert.Equal("same-value", protector.Unprotect(first));
        Assert.Equal("same-value", protector.Unprotect(second));
    }

    /// <summary>
    /// GCM authenticates as well as encrypts. This is what stops a tampered row from decrypting into
    /// an attacker-chosen RTMP target rather than failing outright.
    /// </summary>
    [Fact]
    public void Tampered_ciphertext_is_rejected_rather_than_decrypted()
    {
        var protector = Create();
        var protectedValue = protector.Protect("rtmp-key");

        var parts = protectedValue.Split(':');
        var payload = Convert.FromBase64String(parts[2]);
        payload[^1] ^= 0xFF;
        var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(payload)}";

        var exception = Assert.Throws<DomainException>(() => protector.Unprotect(tampered));
        Assert.Equal(ErrorCodes.SecretProtectionFailed, exception.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-protected-at-all")]
    [InlineData("v1:k1:not-base64!!")]
    [InlineData("v2:k1:AAAA")]
    [InlineData("v1::AAAA")]
    public void Malformed_values_are_rejected(string value)
    {
        var protector = Create();
        Assert.Throws<DomainException>(() => protector.Unprotect(value));
    }

    /// <summary>Decrypting with a different key must fail, not return rubbish.</summary>
    [Fact]
    public void A_value_from_another_key_cannot_be_read()
    {
        var first = Create("k1", new Dictionary<string, string> { ["k1"] = PrimaryKey });
        var protectedValue = first.Protect("secret");

        var other = Create("k2", new Dictionary<string, string> { ["k2"] = SecondKey });

        var exception = Assert.Throws<DomainException>(() => other.Unprotect(protectedValue));
        Assert.Equal(ErrorCodes.SecretProtectionFailed, exception.ErrorCode);
    }

    /// <summary>
    /// Rotation: a new primary encrypts new values while the retired key still decrypts old rows.
    /// Without this, rotating a key would make every stored stream key unreadable.
    /// </summary>
    [Fact]
    public void Rotation_keeps_old_values_readable()
    {
        var beforeRotation = Create("k1", new Dictionary<string, string> { ["k1"] = PrimaryKey });
        var oldValue = beforeRotation.Protect("old-secret");

        var afterRotation = Create("k2", new Dictionary<string, string>
        {
            ["k1"] = PrimaryKey,
            ["k2"] = SecondKey,
        });

        Assert.Equal("old-secret", afterRotation.Unprotect(oldValue));
        Assert.True(afterRotation.NeedsReprotection(oldValue));

        var newValue = afterRotation.Protect("new-secret");
        Assert.False(afterRotation.NeedsReprotection(newValue));
        Assert.StartsWith("v1:k2:", newValue);
    }

    [Fact]
    public void Construction_fails_when_the_primary_key_is_missing() =>
        Assert.Throws<InvalidOperationException>(() =>
            Create("missing", new Dictionary<string, string> { ["k1"] = PrimaryKey }));

    // -----------------------------------------------------------------------------------------
    // Options validation
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Options_reject_a_primary_key_with_no_matching_entry()
    {
        var options = new SecretProtectionOptions
        {
            PrimaryKeyId = "nope",
            Keys = new Dictionary<string, string> { ["k1"] = PrimaryKey },
        };

        Assert.NotEmpty(options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)));
    }

    [Theory]
    [InlineData("AAAA")]
    [InlineData("not base64 at all !!")]
    public void Options_reject_keys_that_are_not_32_bytes(string key)
    {
        var options = new SecretProtectionOptions
        {
            PrimaryKeyId = "k1",
            Keys = new Dictionary<string, string> { ["k1"] = key },
        };

        Assert.NotEmpty(options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)));
    }

    /// <summary>A key id containing the delimiter would corrupt the ciphertext format.</summary>
    [Fact]
    public void Options_reject_a_key_id_containing_the_delimiter()
    {
        var options = new SecretProtectionOptions
        {
            PrimaryKeyId = "a:b",
            Keys = new Dictionary<string, string> { ["a:b"] = PrimaryKey },
        };

        Assert.NotEmpty(options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)));
    }

    [Fact]
    public void A_freshly_generated_key_is_accepted()
    {
        // Documents how an operator is expected to produce one.
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var options = new SecretProtectionOptions
        {
            PrimaryKeyId = "prod-1",
            Keys = new Dictionary<string, string> { ["prod-1"] = key },
        };

        Assert.Empty(options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)));
    }
}

/// <summary>
/// Facebook returns one URL with the key already appended, and the relay needs them apart so the
/// key can be excluded from logs. The split is therefore worth testing directly.
/// </summary>
public class FacebookStreamUrlTests
{
    [Theory]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/FB-123-0-AbCdEf",
        "rtmps://live-api-s.facebook.com:443/rtmp", "FB-123-0-AbCdEf")]
    [InlineData("rtmp://live-api.facebook.com/rtmp/KEY", "rtmp://live-api.facebook.com/rtmp", "KEY")]
    public void Splits_the_endpoint_from_the_key(string streamUrl, string expectedUrl, string expectedKey)
    {
        Assert.True(FacebookAdapter.TrySplitStreamUrl(streamUrl, out var ingestUrl, out var streamKey));
        Assert.Equal(expectedUrl, ingestUrl);
        Assert.Equal(expectedKey, streamKey);
    }

    [Theory]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Rejects_a_url_with_no_key(string streamUrl) =>
        Assert.False(FacebookAdapter.TrySplitStreamUrl(streamUrl, out _, out _));

    /// <summary>The reassembled URL must be byte-identical to what the platform issued.</summary>
    [Fact]
    public void Endpoint_and_key_rejoin_to_the_original()
    {
        const string original = "rtmps://live-api-s.facebook.com:443/rtmp/FB-999-0-XyZ";

        Assert.True(FacebookAdapter.TrySplitStreamUrl(original, out var ingestUrl, out var streamKey));
        Assert.Equal(original, $"{ingestUrl}/{streamKey}");
    }
}

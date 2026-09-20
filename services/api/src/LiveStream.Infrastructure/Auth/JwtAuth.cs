using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LiveStream.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Signing key. Must be supplied through environment variables or a secret store — never
    /// committed. Startup fails if it is missing or too short.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "livestream-api";

    [Required]
    public string Audience { get; set; } = "livestream-web";

    /// <summary>Short access-token lifetime; the client refreshes silently (docs/11-security.md).</summary>
    [Range(60, 3600)]
    public int AccessTokenLifetimeSeconds { get; set; } = 900;
}

/// <summary>
/// Issues short-lived JWT access tokens carrying the user identity only.
///
/// Deliberately uses the system clock rather than the injectable <see cref="IClock"/>: token
/// lifetimes are checked by the JWT validation middleware against the system clock, so issuing
/// against any other time source would produce tokens that are rejected as not-yet-valid or
/// already-expired.
/// </summary>
public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, int ExpiresInSeconds) Issue(Guid userId, string email)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddSeconds(_options.AccessTokenLifetimeSeconds);

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), _options.AccessTokenLifetimeSeconds);
    }
}

/// <summary>
/// Password hashing over ASP.NET Core's <see cref="PasswordHasher{TUser}"/> (PBKDF2-HMAC-SHA512,
/// constant-time comparison). Used standalone rather than pulling in the full Identity stack, which
/// Phase 1 does not need.
/// </summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object HashSubject = new();

    public string Hash(string password) => _hasher.HashPassword(HashSubject, password);

    public (bool Verified, bool NeedsRehash) Verify(string hash, string password)
    {
        var result = _hasher.VerifyHashedPassword(HashSubject, hash, password);
        return result switch
        {
            PasswordVerificationResult.Success => (true, false),
            PasswordVerificationResult.SuccessRehashNeeded => (true, true),
            _ => (false, false),
        };
    }
}

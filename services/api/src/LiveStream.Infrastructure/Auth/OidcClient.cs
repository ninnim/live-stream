using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveStream.Application.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LiveStream.Infrastructure.Auth;

/// <summary>
/// OpenID Connect authorization code flow with PKCE.
///
/// Three things here are load-bearing rather than ceremonial:
///
/// <list type="bullet">
/// <item>The ID token is validated against the provider's published signing keys, its issuer, and
/// this workspace's client id. An unvalidated ID token is just a string the browser handed us, and
/// trusting it would let anyone sign in as anyone.</item>
/// <item>PKCE, even though this is a confidential client with a secret. It costs a hash and it
/// closes code interception at the redirect.</item>
/// <item>The nonce is checked against the one minted when the redirect was issued, which is what
/// stops a token obtained in one sign-in being replayed into another.</item>
/// </list>
///
/// Discovery documents and signing keys are cached per issuer by
/// <see cref="ConfigurationManager{T}"/>, which also handles key rotation — providers roll their
/// keys, and a cache that never refreshed would fail every sign-in the day they did.
/// </summary>
public sealed class OidcClient(IHttpClientFactory httpClientFactory, ILogger<OidcClient> logger) : IOidcClient
{
    public const string HttpClientName = "oidc";

    /// <summary>Clock skew allowed when validating the ID token's lifetime.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configurations =
        new(StringComparer.Ordinal);

    public async Task<OidcAuthorizationRequest> CreateAuthorizationRequestAsync(
        OidcClientConfiguration configuration, string redirectUri, string state,
        CancellationToken cancellationToken)
    {
        var discovery = await GetDiscoveryAsync(configuration.Issuer, cancellationToken);

        if (string.IsNullOrWhiteSpace(discovery.AuthorizationEndpoint))
        {
            throw new OidcException($"Issuer {configuration.Issuer} published no authorization endpoint.");
        }

        var codeVerifier = RandomUrlSafe(64);
        var nonce = RandomUrlSafe(32);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))),
            ["code_challenge_method"] = "S256",
        };

        var url = discovery.AuthorizationEndpoint
                  + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                  + string.Join("&", query.Select(pair =>
                      $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));

        return new OidcAuthorizationRequest(url, codeVerifier, nonce);
    }

    public async Task<OidcIdentity> ExchangeCodeAsync(OidcClientConfiguration configuration, string redirectUri,
        string code, string codeVerifier, string nonce, CancellationToken cancellationToken)
    {
        var discovery = await GetDiscoveryAsync(configuration.Issuer, cancellationToken);

        if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
        {
            throw new OidcException($"Issuer {configuration.Issuer} published no token endpoint.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = configuration.ClientId,
                ["client_secret"] = configuration.ClientSecret,
                ["code_verifier"] = codeVerifier,
            }),
        };

        TokenResponse? token;

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // The provider's error body can carry the code and other request detail, so only
                // the status is recorded.
                throw new OidcException(
                    $"Token endpoint for {configuration.Issuer} returned {(int)response.StatusCode}.");
            }

            token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new OidcException($"Token exchange with {configuration.Issuer} failed.", ex);
        }

        if (string.IsNullOrWhiteSpace(token?.IdToken))
        {
            throw new OidcException($"Token endpoint for {configuration.Issuer} returned no ID token.");
        }

        return await ValidateIdTokenAsync(configuration, discovery, token.IdToken, nonce, cancellationToken);
    }

    private async Task<OidcIdentity> ValidateIdTokenAsync(OidcClientConfiguration configuration,
        OpenIdConnectConfiguration discovery, string idToken, string nonce, CancellationToken cancellationToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer ?? configuration.Issuer,
            ValidateIssuer = true,
            ValidAudience = configuration.ClientId,
            ValidateAudience = true,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);

        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "ID token from {Issuer} failed validation", configuration.Issuer);
            throw new OidcException($"ID token from {configuration.Issuer} failed validation.", result.Exception);
        }

        var claims = result.Claims;

        // A provider that returned a token for a different sign-in, or replayed an old one, fails
        // here. The nonce is the only thing tying this token to the redirect we issued.
        if (!string.Equals(StringClaim(claims, "nonce"), nonce, StringComparison.Ordinal))
        {
            throw new OidcException($"ID token from {configuration.Issuer} carried the wrong nonce.");
        }

        var subject = StringClaim(claims, "sub");

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new OidcException($"ID token from {configuration.Issuer} carried no subject.");
        }

        var email = StringClaim(claims, "email");
        var emailVerified = claims.TryGetValue("email_verified", out var verified)
                            && verified switch
                            {
                                bool flag => flag,
                                string text => bool.TryParse(text, out var parsed) && parsed,
                                _ => false,
                            };

        await Task.CompletedTask;
        return new OidcIdentity(subject, email?.Trim().ToLowerInvariant(), emailVerified, StringClaim(claims, "name"));
    }

    private async Task<OpenIdConnectConfiguration> GetDiscoveryAsync(string issuer, CancellationToken cancellationToken)
    {
        var manager = _configurations.GetOrAdd(issuer, key => new ConfigurationManager<OpenIdConnectConfiguration>(
            key.TrimEnd('/') + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClientFactory.CreateClient(HttpClientName))
            {
                // Discovery and JWKS are fetched over the network; plain HTTP would let anyone on
                // the path choose the signing keys, and therefore who may sign in.
                RequireHttps = true,
            }));

        try
        {
            return await manager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OidcException($"Discovery for {issuer} failed.", ex);
        }
    }

    private static string? StringClaim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static string RandomUrlSafe(int bytes) =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    private sealed record TokenResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
    }
}

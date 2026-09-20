namespace LiveStream.Application.Abstractions;

/// <summary>
/// The control plane's only view of an external identity provider.
///
/// Everything protocol-specific — discovery, JWKS, PKCE, ID token validation — lives behind this
/// boundary, which is what lets the sign-in flow, the account linking rules, and the workspace
/// checks be tested without an identity provider to talk to.
/// </summary>
public interface IOidcClient
{
    /// <summary>
    /// Builds the URL to send the browser to, plus the PKCE verifier and nonce that the callback
    /// will be checked against. Both are generated here and stored server-side: a caller that could
    /// choose them could replay someone else's authorization code.
    /// </summary>
    Task<OidcAuthorizationRequest> CreateAuthorizationRequestAsync(OidcClientConfiguration configuration,
        string redirectUri, string state, CancellationToken cancellationToken);

    /// <summary>
    /// Exchanges an authorization code for a verified identity: signature, issuer, audience,
    /// expiry, and nonce all checked. Throws <see cref="OidcException"/> when any of that fails.
    /// </summary>
    Task<OidcIdentity> ExchangeCodeAsync(OidcClientConfiguration configuration, string redirectUri, string code,
        string codeVerifier, string nonce, CancellationToken cancellationToken);
}

/// <summary>What the client needs to talk to one provider. The secret never leaves the server.</summary>
public sealed record OidcClientConfiguration(string Issuer, string ClientId, string ClientSecret);

public sealed record OidcAuthorizationRequest(string AuthorizationUrl, string CodeVerifier, string Nonce);

/// <summary>
/// A verified identity. <see cref="Subject"/> is the provider's stable identifier and the only
/// thing accounts are keyed on; the email is used to decide which workspace the person belongs to.
/// </summary>
public sealed record OidcIdentity(string Subject, string? Email, bool EmailVerified, string? Name);

/// <summary>
/// A single-sign-on failure. The message is for logs and operators; what reaches the browser is one
/// generic error, so a probe cannot learn which step it got past.
/// </summary>
public sealed class OidcException(string message, Exception? innerException = null)
    : Exception(message, innerException);

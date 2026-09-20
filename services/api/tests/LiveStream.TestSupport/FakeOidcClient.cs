using System.Collections.Concurrent;
using LiveStream.Application.Abstractions;

namespace LiveStream.TestSupport;

/// <summary>
/// Stands in for an external identity provider, the same way <see cref="FakeMediaGateway"/> stands
/// in for the media gateway: the provider is somebody else's process, but everything this platform
/// decides around it — domain verification, account linking, provisioning, membership — is
/// exercised for real.
///
/// It also keeps the PKCE verifier and nonce it issued, so a test can assert that the callback is
/// checked against the redirect that started it rather than against whatever it was handed.
/// </summary>
public sealed class FakeOidcClient : IOidcClient
{
    private readonly ConcurrentDictionary<string, (string CodeVerifier, string Nonce)> _issued = new();

    /// <summary>Identity the next successful exchange returns.</summary>
    public OidcIdentity Identity { get; set; } = new("subject-1", "person@example.com", true, "Test Person");

    /// <summary>When set, the next call throws it — a provider that is down, or refuses.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Authorization codes this fake will accept. Anything else is rejected.</summary>
    public string ValidCode { get; set; } = "valid-code";

    public int ExchangeCount { get; private set; }

    /// <summary>The most recent redirect URI the platform asked the provider to use.</summary>
    public string? LastRedirectUri { get; private set; }

    public Task<OidcAuthorizationRequest> CreateAuthorizationRequestAsync(OidcClientConfiguration configuration,
        string redirectUri, string state, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        LastRedirectUri = redirectUri;

        var verifier = $"verifier-{state}";
        var nonce = $"nonce-{state}";
        _issued[state] = (verifier, nonce);

        var url = $"{configuration.Issuer}/authorize?client_id={configuration.ClientId}&state={state}";
        return Task.FromResult(new OidcAuthorizationRequest(url, verifier, nonce));
    }

    public Task<OidcIdentity> ExchangeCodeAsync(OidcClientConfiguration configuration, string redirectUri,
        string code, string codeVerifier, string nonce, CancellationToken cancellationToken)
    {
        ExchangeCount += 1;

        if (Failure is not null)
        {
            throw Failure;
        }

        if (code != ValidCode)
        {
            throw new OidcException("Unknown authorization code.");
        }

        // The verifier and nonce must be the pair this fake issued for some sign-in. A platform
        // that passed through whatever the callback supplied would fail here.
        if (!_issued.Values.Any(issued => issued.CodeVerifier == codeVerifier && issued.Nonce == nonce))
        {
            throw new OidcException("The code verifier and nonce do not match any request we issued.");
        }

        return Task.FromResult(Identity);
    }
}

namespace LiveStream.Application.Abstractions;

/// <summary>
/// Short-lived storage for a sign-in that is in flight.
///
/// Separate from <see cref="IOAuthStateStore"/>, which links a provider account on behalf of a
/// signed-in operator. This one runs before anybody is signed in and carries the PKCE verifier and
/// the nonce, neither of which may ever come from the caller.
///
/// Entries are single-use: <see cref="ConsumeAsync"/> reads and removes, so a replayed callback
/// finds nothing.
/// </summary>
public interface ISsoLoginStateStore
{
    Task StoreAsync(string state, SsoLoginState entry, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<SsoLoginState?> ConsumeAsync(string state, CancellationToken cancellationToken);
}

/// <summary>
/// Everything the callback needs, recorded when the redirect was issued. The callback supplies only
/// the state and the code; if it could supply the connection, the verifier, or the redirect URI, it
/// could aim a valid code at a provider of its choosing.
/// </summary>
public sealed record SsoLoginState(
    Guid ConnectionId,
    Guid WorkspaceId,
    string RedirectUri,
    string CodeVerifier,
    string Nonce,
    DateTimeOffset CreatedAt);

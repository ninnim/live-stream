namespace LiveStream.Application.Abstractions;

/// <summary>
/// Short-lived storage for the OAuth <c>state</c> value.
///
/// State is what ties an authorization redirect to the callback that follows it. Without it, an
/// attacker can hand a victim a crafted callback URL and silently link their own channel to the
/// victim workspace — every subsequent broadcast would then republish to the attacker account.
///
/// Entries are single-use: <see cref="ConsumeAsync"/> both reads and removes, so a replayed callback
/// finds nothing.
/// </summary>
public interface IOAuthStateStore
{
    Task StoreAsync(string state, OAuthStateEntry entry, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Reads and removes an entry. Returns <c>null</c> when unknown, expired, or already used.</summary>
    Task<OAuthStateEntry?> ConsumeAsync(string state, CancellationToken cancellationToken);
}

/// <summary>
/// What the callback needs to know, recorded when the redirect was issued rather than accepted from
/// the caller — the whole point is that the callback cannot choose these.
/// </summary>
public sealed record OAuthStateEntry(
    Guid WorkspaceId,
    Guid UserId,
    string Provider,
    string RedirectUri,
    DateTimeOffset CreatedAt);

using LiveStream.Domain.Common;

namespace LiveStream.Domain.Distribution;

public enum ProviderAccountStatus
{
    /// <summary>Tokens are present and believed valid.</summary>
    Connected = 0,

    /// <summary>The refresh token was rejected. The operator must link the account again.</summary>
    NeedsReauthorization = 1,

    /// <summary>Operator disconnected the account. Tokens have been erased.</summary>
    Revoked = 2,
}

/// <summary>
/// An external platform account linked to a workspace through OAuth.
///
/// Workspace-scoped rather than session-scoped on purpose: an operator links their channel once and
/// reuses it for every broadcast. Tokens are stored encrypted and are never projected into an API
/// response (docs/11-security.md: "Never log OAuth refresh tokens").
/// </summary>
public class ProviderAccount
{
    private readonly List<StreamDestination> _destinations = [];

    private ProviderAccount()
    {
        ExternalAccountId = string.Empty;
        DisplayName = string.Empty;
        AccessTokenCipher = string.Empty;
        Scopes = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public DestinationProvider Provider { get; private set; }

    /// <summary>The platform's own identifier for the account. Used to detect re-linking the same channel.</summary>
    public string ExternalAccountId { get; private set; }

    /// <summary>Channel or page name, shown in the UI so an operator can tell two accounts apart.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Encrypted access token. Never returned by any endpoint.</summary>
    public string AccessTokenCipher { get; private set; }

    /// <summary>Encrypted refresh token, when the provider issues one. Never returned by any endpoint.</summary>
    public string? RefreshTokenCipher { get; private set; }

    /// <summary>When the access token expires. Refreshed ahead of this by the provider adapter.</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; private set; }

    /// <summary>Space-separated scopes actually granted, which may be narrower than those requested.</summary>
    public string Scopes { get; private set; }

    public ProviderAccountStatus Status { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public Guid LinkedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? LastRefreshedAt { get; private set; }

    public IReadOnlyCollection<StreamDestination> Destinations => _destinations;

    public static ProviderAccount Link(
        Guid workspaceId,
        DestinationProvider provider,
        string externalAccountId,
        string displayName,
        string accessTokenCipher,
        string? refreshTokenCipher,
        DateTimeOffset? accessTokenExpiresAt,
        string scopes,
        Guid linkedByUserId,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalAccountId))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The provider returned no account identifier.");
        }

        return new ProviderAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Provider = provider,
            ExternalAccountId = externalAccountId.Trim(),
            DisplayName = Truncate(displayName, 200),
            AccessTokenCipher = accessTokenCipher,
            RefreshTokenCipher = refreshTokenCipher,
            AccessTokenExpiresAt = accessTokenExpiresAt,
            Scopes = Truncate(scopes, 1000),
            Status = ProviderAccountStatus.Connected,
            LinkedByUserId = linkedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
            LastRefreshedAt = now,
        };
    }

    /// <summary>Replaces the stored tokens after a re-link or a successful refresh.</summary>
    public void UpdateTokens(string accessTokenCipher, string? refreshTokenCipher,
        DateTimeOffset? accessTokenExpiresAt, DateTimeOffset now)
    {
        AccessTokenCipher = accessTokenCipher;

        // Providers commonly omit the refresh token on a refresh response. Keeping the existing one
        // is required — overwriting it with null would silently un-link the account on next use.
        if (!string.IsNullOrEmpty(refreshTokenCipher))
        {
            RefreshTokenCipher = refreshTokenCipher;
        }

        AccessTokenExpiresAt = accessTokenExpiresAt;
        Status = ProviderAccountStatus.Connected;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastRefreshedAt = now;
        UpdatedAt = now;
    }

    public void UpdateDisplayName(string displayName, DateTimeOffset now)
    {
        DisplayName = Truncate(displayName, 200);
        UpdatedAt = now;
    }

    /// <summary>Marks the account as needing a fresh consent, without discarding it from the workspace.</summary>
    public void MarkNeedsReauthorization(string errorCode, string message, DateTimeOffset now)
    {
        Status = ProviderAccountStatus.NeedsReauthorization;
        LastErrorCode = Truncate(errorCode, 64);
        LastErrorMessage = Truncate(message, 500);
        UpdatedAt = now;
    }

    /// <summary>
    /// Disconnects the account and erases both tokens. Erasure is the point: a revoked account must
    /// leave no usable secret behind, even in a soft-deleted row.
    /// </summary>
    public void Revoke(DateTimeOffset now)
    {
        Status = ProviderAccountStatus.Revoked;
        AccessTokenCipher = string.Empty;
        RefreshTokenCipher = null;
        AccessTokenExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>True when the access token is missing or within <paramref name="skew"/> of expiry.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew) =>
        Status == ProviderAccountStatus.Connected
        && (string.IsNullOrEmpty(AccessTokenCipher) || (AccessTokenExpiresAt is { } expiry && now.Add(skew) >= expiry));

    public bool IsUsable => Status == ProviderAccountStatus.Connected && !string.IsNullOrEmpty(AccessTokenCipher);

    private static string Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

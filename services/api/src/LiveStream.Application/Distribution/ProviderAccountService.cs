using System.Security.Cryptography;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Distribution;

/// <summary>
/// Links external platform accounts to a workspace over OAuth, and keeps their tokens usable.
///
/// Tokens are encrypted the moment they arrive and decrypted only when a provider call needs one.
/// No method here returns a token, and no response contract has a field one could travel in.
/// </summary>
public sealed class ProviderAccountService(
    IAppDbContext db,
    LiveSessionAuthorizationService authorization,
    IDestinationProviderRegistry providers,
    ISecretProtector secretProtector,
    IOAuthStateStore stateStore,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<DistributionOptions> options,
    ILogger<ProviderAccountService> logger)
{
    private readonly DistributionOptions _options = options.Value;

    public async Task<IReadOnlyList<ProviderAccountResponse>> ListAsync(Guid workspaceId, Guid userId,
        CancellationToken cancellationToken)
    {
        await EnsureWorkspaceAccessAsync(workspaceId, userId, WorkspacePermission.LiveSessionView, cancellationToken);

        var accounts = await db.ProviderAccounts
            .AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.Status != ProviderAccountStatus.Revoked)
            .OrderBy(a => a.Provider)
            .ThenBy(a => a.DisplayName)
            .ToListAsync(cancellationToken);

        return accounts.Select(DestinationMapper.ToResponse).ToList();
    }

    /// <summary>
    /// Starts account linking. Returns the provider consent URL plus the single-use state value the
    /// caller must present on the way back.
    /// </summary>
    public async Task<ProviderAuthorizationResponse> BeginAuthorizationAsync(Guid workspaceId, Guid userId,
        string providerName, string redirectUri, CancellationToken cancellationToken)
    {
        await EnsureWorkspaceAccessAsync(workspaceId, userId, WorkspacePermission.DestinationManage, cancellationToken);

        var provider = ParseProvider(providerName);
        var client = RequireOAuthClient(provider);

        var state = GenerateState();
        var now = clock.UtcNow;

        await stateStore.StoreAsync(state,
            new OAuthStateEntry(workspaceId, userId, provider.ToString(), redirectUri, now),
            _options.OAuthStateLifetime, cancellationToken);

        var url = client.BuildAuthorizationUrl(state, redirectUri);

        logger.LogInformation(
            "Provider authorization started workspace={WorkspaceId} provider={Provider} correlationId={CorrelationId}",
            workspaceId, provider, correlation.CorrelationId);

        return new ProviderAuthorizationResponse(url.ToString(), state, now.Add(_options.OAuthStateLifetime));
    }

    /// <summary>
    /// Completes account linking by exchanging the authorization code for tokens.
    ///
    /// The workspace comes from the stored state entry, never from the request: that binding is what
    /// stops a crafted callback attaching an attacker channel to someone else workspace.
    /// </summary>
    public async Task<ProviderAccountResponse> CompleteAuthorizationAsync(Guid userId,
        CompleteProviderAuthorizationRequest request, CancellationToken cancellationToken)
    {
        var entry = await stateStore.ConsumeAsync(request.State, cancellationToken)
            ?? throw new DomainException(ErrorCodes.ValidationFailed,
                "This authorization link has expired or was already used. Start linking the account again.");

        if (entry.UserId != userId)
        {
            logger.LogWarning(
                "OAuth callback user mismatch: state issued to {IssuedTo}, presented by {PresentedBy}",
                entry.UserId, userId);
            throw new DomainException(ErrorCodes.PermissionDenied,
                "This authorization link was issued to a different user.");
        }

        var provider = ParseProvider(entry.Provider);
        var client = RequireOAuthClient(provider);

        await EnsureWorkspaceAccessAsync(entry.WorkspaceId, userId, WorkspacePermission.DestinationManage,
            cancellationToken);

        ProviderTokenSet tokens;
        ProviderAccountProfile profile;
        try
        {
            tokens = await client.ExchangeCodeAsync(request.Code, entry.RedirectUri, cancellationToken);
            profile = await client.GetProfileAsync(tokens.AccessToken, cancellationToken);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception may carry provider response text; log the type and message only through
            // the structured logger, and never surface it to the client verbatim.
            logger.LogError(ex, "Token exchange failed for provider {Provider}", provider);
            throw new DomainException(ErrorCodes.ProviderApiError,
                $"{provider} did not complete the connection. Try linking the account again.");
        }

        var now = clock.UtcNow;

        // Re-linking the same channel updates the existing row rather than creating a duplicate,
        // so destinations already pointing at it keep working.
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(
            a => a.WorkspaceId == entry.WorkspaceId
                 && a.Provider == provider
                 && a.ExternalAccountId == profile.ExternalAccountId,
            cancellationToken);

        if (account is null)
        {
            account = ProviderAccount.Link(
                entry.WorkspaceId, provider, profile.ExternalAccountId, profile.DisplayName,
                secretProtector.Protect(tokens.AccessToken),
                tokens.RefreshToken is null ? null : secretProtector.Protect(tokens.RefreshToken),
                tokens.ExpiresAt, tokens.Scopes, userId, now);

            db.ProviderAccounts.Add(account);
        }
        else
        {
            account.UpdateTokens(
                secretProtector.Protect(tokens.AccessToken),
                tokens.RefreshToken is null ? null : secretProtector.Protect(tokens.RefreshToken),
                tokens.ExpiresAt, now);
            account.UpdateDisplayName(profile.DisplayName, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Provider account linked workspace={WorkspaceId} provider={Provider} accountId={AccountId}",
            entry.WorkspaceId, provider, account.Id);

        return DestinationMapper.ToResponse(account);
    }

    /// <summary>Disconnects an account and erases its tokens, locally and at the provider where possible.</summary>
    public async Task DisconnectAsync(Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            ?? throw new DomainException(ErrorCodes.ProviderAccountUnavailable, "That linked account no longer exists.");

        await EnsureWorkspaceAccessAsync(account.WorkspaceId, userId, WorkspacePermission.DestinationManage,
            cancellationToken);

        if (account.RefreshTokenCipher is { } refreshCipher
            && providers.TryGetOAuthClient(account.Provider, out var client))
        {
            try
            {
                await client.RevokeAsync(secretProtector.Unprotect(refreshCipher), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider-side revocation is best effort. Local erasure below is what actually
                // protects the workspace, and it must happen regardless.
                logger.LogWarning(ex, "Provider-side revocation failed for account {AccountId}", accountId);
            }
        }

        account.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Provider account disconnected accountId={AccountId} provider={Provider}",
            accountId, account.Provider);
    }

    /// <summary>
    /// Returns a usable access token, refreshing it first when it is at or near expiry.
    ///
    /// Refreshing ahead of expiry rather than reacting to a 401 matters here: the alternative is
    /// discovering the token is dead at the moment a broadcast is starting.
    /// </summary>
    public async Task<string?> GetUsableAccessTokenAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        if (account.Status != ProviderAccountStatus.Connected)
        {
            return null;
        }

        var now = clock.UtcNow;

        if (!account.NeedsRefresh(now, _options.TokenRefreshSkew))
        {
            return secretProtector.Unprotect(account.AccessTokenCipher);
        }

        if (account.RefreshTokenCipher is not { } refreshCipher)
        {
            account.MarkNeedsReauthorization(ErrorCodes.ProviderAccountUnavailable,
                "The access token expired and no refresh token is stored.", now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (!providers.TryGetOAuthClient(account.Provider, out var client))
        {
            return null;
        }

        try
        {
            var tokens = await client.RefreshAsync(secretProtector.Unprotect(refreshCipher), cancellationToken);

            account.UpdateTokens(
                secretProtector.Protect(tokens.AccessToken),
                tokens.RefreshToken is null ? null : secretProtector.Protect(tokens.RefreshToken),
                tokens.ExpiresAt, clock.UtcNow);

            await db.SaveChangesAsync(cancellationToken);

            return tokens.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Token refresh failed for account {AccountId} provider {Provider}",
                account.Id, account.Provider);

            account.MarkNeedsReauthorization(ErrorCodes.ProviderAccountUnavailable,
                "The platform rejected the stored credentials.", clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            return null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task EnsureWorkspaceAccessAsync(Guid workspaceId, Guid userId, WorkspacePermission permission,
        CancellationToken cancellationToken)
    {
        var role = await authorization.GetRoleAsync(workspaceId, userId, cancellationToken);
        if (role is null || !WorkspacePermissions.Allows(role.Value, permission))
        {
            throw new DomainException(ErrorCodes.PermissionDenied, "You do not have access to this workspace.");
        }
    }

    private IProviderOAuthClient RequireOAuthClient(DestinationProvider provider)
    {
        if (!providers.TryGetOAuthClient(provider, out var client))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"{provider} does not support linked accounts.");
        }

        if (!client.IsConfigured)
        {
            // A deployment without provider credentials must say so plainly rather than sending the
            // operator to a broken consent screen.
            throw new DomainException(ErrorCodes.ProviderAccountUnavailable,
                $"{provider} account linking is not configured on this server. " +
                "Add the provider client credentials, or use a stream key instead.");
        }

        return client;
    }

    private static DestinationProvider ParseProvider(string? value)
    {
        if (!Enum.TryParse<DestinationProvider>(value, ignoreCase: true, out var provider))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown destination provider '{value}'.");
        }

        return provider;
    }

    /// <summary>128 bits of entropy, URL-safe. Guessing a state value must not be feasible.</summary>
    private static string GenerateState()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

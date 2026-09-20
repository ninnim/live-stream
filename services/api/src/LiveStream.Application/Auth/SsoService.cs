using System.Security.Cryptography;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Application.Governance.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Auth;

/// <summary>
/// Enterprise single sign-on (implementation/phase-7: "SSO").
///
/// The security model in one line: <em>a workspace may claim an email domain, but only an operator
/// can verify it, and only a verified domain can sign anybody in.</em> Whoever holds a domain
/// decides who may sign in with an address there, so a self-service claim on a domain you do not
/// own would be an account-takeover primitive rather than a configuration mistake.
///
/// Two further rules follow from the same reasoning:
/// <list type="bullet">
/// <item>Accounts are keyed on the provider's subject claim, never on email. An address can be
/// reassigned inside a company, and matching on it would hand the new holder the old holder's
/// account.</item>
/// <item>Just-in-time provisioning cannot grant Owner or Admin, so an identity provider can never
/// mint someone able to reconfigure the identity provider.</item>
/// </list>
/// </summary>
public sealed class SsoService(
    IAppDbContext db,
    IOidcClient oidc,
    ISsoLoginStateStore stateStore,
    ISecretProtector secretProtector,
    AuthService authService,
    WorkspaceAuthorizationService authorization,
    TenantLimitService tenantLimits,
    IClock clock,
    IOptions<SsoOptions> options,
    ILogger<SsoService> logger)
{
    private readonly SsoOptions _options = options.Value;

    // -----------------------------------------------------------------------------------------
    // Administration
    // -----------------------------------------------------------------------------------------

    public async Task<SsoConnectionResponse?> GetConnectionAsync(Guid workspaceId, Guid userId,
        CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var connection = await db.SsoConnections
            .AsNoTracking()
            .Include(c => c.Domains)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, cancellationToken);

        return connection is null ? null : Project(connection);
    }

    public async Task<SsoConnectionResponse> UpsertConnectionAsync(Guid workspaceId, Guid userId,
        UpsertSsoConnectionRequest request, CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var limits = await tenantLimits.ResolveAsync(workspaceId, cancellationToken);

        if (!limits.SingleSignOnAllowed)
        {
            throw new DomainException(ErrorCodes.PlanLimitReached,
                $"Single sign-on is not part of the {limits.Plan} plan.");
        }

        if (!Enum.TryParse<WorkspaceRole>(request.DefaultRole, ignoreCase: true, out var defaultRole))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown role '{request.DefaultRole}'.");
        }

        var now = clock.UtcNow;

        var connection = await db.SsoConnections
            .Include(c => c.Domains)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, cancellationToken);

        var isNew = connection is null;

        if (connection is null)
        {
            connection = new WorkspaceSsoConnection { WorkspaceId = workspaceId, CreatedAt = now };
            db.SsoConnections.Add(connection);
        }

        connection.Configure(request.Issuer, request.ClientId, defaultRole, request.Enabled,
            request.JitProvisioning, now);

        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            connection.ClientSecretCiphertext = secretProtector.Protect(request.ClientSecret);
        }
        else if (isNew)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A client secret is required.");
        }

        await ApplyDomainsAsync(connection, request.Domains, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SSO connection saved {WorkspaceId} issuer={Issuer} enabled={Enabled} domains={Domains}",
            workspaceId, connection.Issuer, connection.Enabled, connection.Domains.Count);

        return Project(connection);
    }

    public async Task DeleteConnectionAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var connection = await db.SsoConnections.FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId,
            cancellationToken);

        if (connection is null)
        {
            return; // Idempotent: the desired state is "no connection".
        }

        db.SsoConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);

        // The accounts themselves are left alone: removing a connection is not the same as
        // removing the people who used it. Anyone who registered with a password can still
        // sign in with it — but anyone provisioned by this connection has no password, and
        // this locks them out until an operator restores the connection.
        logger.LogInformation("SSO connection removed {WorkspaceId}", workspaceId);
    }

    /// <summary>
    /// Marks a claimed domain verified, or withdraws that verification. Operator-only, and reached
    /// through the internal operations API: this is the step that decides whether a workspace may
    /// sign in everybody at an email domain.
    /// </summary>
    public async Task<SsoDomainResponse> SetDomainVerificationAsync(string domain, bool verified,
        CancellationToken cancellationToken)
    {
        var normalized = WorkspaceSsoDomain.Normalize(domain);

        var row = await db.SsoDomains.FirstOrDefaultAsync(d => d.Domain == normalized, cancellationToken)
                  ?? throw new DomainException(ErrorCodes.SsoNotConfigured,
                      $"No workspace has claimed {normalized}.");

        row.VerifiedAt = verified ? clock.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("SSO domain {Domain} verification set to {Verified} for connection {ConnectionId}",
            normalized, verified, row.ConnectionId);

        return new SsoDomainResponse(row.Domain, row.VerifiedAt is not null, row.VerifiedAt);
    }

    // -----------------------------------------------------------------------------------------
    // Sign-in
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Whether an address signs in through an identity provider.
    ///
    /// Answers the same way for a domain nobody has claimed and for one whose account does not
    /// exist, because this endpoint is unauthenticated: it must not become a way to discover which
    /// companies use the platform or which addresses are registered.
    /// </summary>
    public async Task<SsoDiscoveryResponse> DiscoverAsync(string? email, CancellationToken cancellationToken)
    {
        var connection = await FindUsableConnectionAsync(email, cancellationToken);

        if (connection is null)
        {
            return new SsoDiscoveryResponse(false, null);
        }

        var workspaceName = await db.Workspaces
            .AsNoTracking()
            .Where(w => w.Id == connection.WorkspaceId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new SsoDiscoveryResponse(true, workspaceName);
    }

    public async Task<SsoStartResponse> StartAsync(string? email, CancellationToken cancellationToken)
    {
        var connection = await FindUsableConnectionAsync(email, cancellationToken)
                         ?? throw new DomainException(ErrorCodes.SsoNotConfigured,
                             "Single sign-on is not set up for that address.");

        var configuration = BuildClientConfiguration(connection);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        OidcAuthorizationRequest authorization;

        try
        {
            authorization = await oidc.CreateAuthorizationRequestAsync(configuration, _options.RedirectUri, state,
                cancellationToken);
        }
        catch (OidcException ex)
        {
            logger.LogError(ex, "SSO start failed for workspace {WorkspaceId}", connection.WorkspaceId);
            throw new DomainException(ErrorCodes.SsoFailed,
                "We could not reach your identity provider. Try again, or sign in with a password.");
        }

        await stateStore.StoreAsync(state,
            new SsoLoginState(connection.Id, connection.WorkspaceId, _options.RedirectUri,
                authorization.CodeVerifier, authorization.Nonce, clock.UtcNow),
            _options.LoginTimeout, cancellationToken);

        return new SsoStartResponse(authorization.AuthorizationUrl, state);
    }

    /// <summary>
    /// Completes a sign-in: validates the code against the state that started it, then finds or
    /// creates the account.
    ///
    /// Every failure surfaces as one error code. Distinguishing "unknown state" from "bad code"
    /// from "domain not verified" would tell someone probing the endpoint exactly how far they got.
    /// </summary>
    public async Task<AuthResponse> CompleteAsync(string? state, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
        {
            throw SsoFailed();
        }

        var login = await stateStore.ConsumeAsync(state, cancellationToken) ?? throw SsoFailed();

        if (clock.UtcNow - login.CreatedAt > WorkspaceSsoConnection.LoginTimeout)
        {
            throw SsoFailed();
        }

        var connection = await db.SsoConnections
            .Include(c => c.Domains)
            .FirstOrDefaultAsync(c => c.Id == login.ConnectionId, cancellationToken);

        // Re-checked after the redirect: an admin may have switched the connection off, or an
        // operator withdrawn a domain, while the browser was away at the provider.
        if (connection is null || !connection.IsUsable)
        {
            throw SsoFailed();
        }

        OidcIdentity identity;

        try
        {
            identity = await oidc.ExchangeCodeAsync(BuildClientConfiguration(connection), login.RedirectUri, code,
                login.CodeVerifier, login.Nonce, cancellationToken);
        }
        catch (OidcException ex)
        {
            logger.LogWarning(ex, "SSO code exchange failed for workspace {WorkspaceId}", connection.WorkspaceId);
            throw SsoFailed();
        }

        var user = await ResolveUserAsync(connection, identity, cancellationToken);

        connection.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("SSO sign-in {UserId} workspace={WorkspaceId} issuer={Issuer}",
            user.Id, connection.WorkspaceId, connection.Issuer);

        return await authService.IssueSessionAsync(user, cancellationToken);
    }

    // -----------------------------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Finds or creates the account behind a verified identity.
    ///
    /// The order matters. An existing link by subject wins outright. Otherwise the address has to
    /// be inside a domain this connection has verified — which is what makes linking to an account
    /// that already has a password safe: the workspace has proved it owns the domain that address
    /// lives in.
    /// </summary>
    private async Task<User> ResolveUserAsync(WorkspaceSsoConnection connection, OidcIdentity identity,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var link = await db.UserIdentities
            .Include(i => i.User)
            .FirstOrDefaultAsync(i => i.Issuer == connection.Issuer && i.Subject == identity.Subject,
                cancellationToken);

        if (link?.User is not null)
        {
            if (link.User.Status is UserStatus.Suspended)
            {
                throw new DomainException(ErrorCodes.PermissionDenied, "This account is suspended.");
            }

            link.LastLoginAt = now;
            await EnsureMembershipAsync(connection, link.User, now, cancellationToken);
            return link.User;
        }

        var email = identity.Email;
        var domain = WorkspaceSsoDomain.FromEmail(email);

        // An unverified address is refused: a provider that does not vouch for the address cannot
        // be used to claim the account that owns it.
        if (email is null || domain is null || !identity.EmailVerified)
        {
            logger.LogWarning("SSO identity from {Issuer} carried no verified email", connection.Issuer);
            throw SsoFailed();
        }

        if (!connection.Domains.Any(d => d.VerifiedAt is not null && d.Domain == domain))
        {
            logger.LogWarning("SSO identity from {Issuer} is outside every verified domain", connection.Issuer);
            throw SsoFailed();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            if (!connection.JitProvisioning)
            {
                throw new DomainException(ErrorCodes.SsoFailed,
                    "There is no account for that address. Ask an administrator to invite you.");
            }

            user = new User
            {
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(identity.Name) ? email.Split('@')[0] : identity.Name.Trim(),

                // No password is set, and none can be guessed: the hash is not a hash of
                // anything, and LoginAsync refuses an empty one outright. This account can
                // therefore only be reached through its identity provider — there is no
                // password reset flow to fall back to (docs/implementation-notes/phase-7-report.md).
                PasswordHash = string.Empty,
                Status = UserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.Users.Add(user);
        }
        else if (user.Status is UserStatus.Suspended)
        {
            throw new DomainException(ErrorCodes.PermissionDenied, "This account is suspended.");
        }

        db.UserIdentities.Add(new UserIdentity
        {
            UserId = user.Id,
            Issuer = connection.Issuer,
            Subject = identity.Subject,
            CreatedAt = now,
            LastLoginAt = now,
        });

        await EnsureMembershipAsync(connection, user, now, cancellationToken);
        return user;
    }

    /// <summary>
    /// Adds the member to the workspace on first sign-in. An existing membership is left exactly as
    /// it is: a person promoted to Admin must not be demoted to the connection's default role every
    /// time they sign in.
    /// </summary>
    private async Task EnsureMembershipAsync(WorkspaceSsoConnection connection, User user, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exists = await db.WorkspaceMembers
            .AnyAsync(m => m.WorkspaceId == connection.WorkspaceId && m.UserId == user.Id, cancellationToken);

        if (exists)
        {
            return;
        }

        if (!connection.JitProvisioning)
        {
            throw new DomainException(ErrorCodes.SsoFailed,
                "You are not a member of that workspace. Ask an administrator to invite you.");
        }

        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = connection.WorkspaceId,
            UserId = user.Id,
            Role = connection.DefaultRole,
            CreatedAt = now,
        });
    }

    private async Task<WorkspaceSsoConnection?> FindUsableConnectionAsync(string? email,
        CancellationToken cancellationToken)
    {
        var domain = WorkspaceSsoDomain.FromEmail(email);

        if (domain is null)
        {
            return null;
        }

        var connectionId = await db.SsoDomains
            .AsNoTracking()
            .Where(d => d.Domain == domain && d.VerifiedAt != null)
            .Select(d => (Guid?)d.ConnectionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (connectionId is null)
        {
            return null;
        }

        var connection = await db.SsoConnections
            .Include(c => c.Domains)
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.Enabled, cancellationToken);

        return connection?.IsUsable is true ? connection : null;
    }

    private async Task ApplyDomainsAsync(WorkspaceSsoConnection connection, IReadOnlyList<string>? requested,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var domains = (requested ?? [])
            .Select(WorkspaceSsoDomain.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (domains.Count is 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Claim at least one email domain.");
        }

        if (domains.Count > WorkspaceSsoConnection.MaxDomains)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"A connection can claim at most {WorkspaceSsoConnection.MaxDomains} domains.");
        }

        var takenElsewhere = await db.SsoDomains
            .AsNoTracking()
            .Where(d => domains.Contains(d.Domain) && d.ConnectionId != connection.Id)
            .Select(d => d.Domain)
            .ToListAsync(cancellationToken);

        if (takenElsewhere.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"Another workspace has already claimed {string.Join(", ", takenElsewhere)}.");
        }

        // Removed domains go, added ones arrive unverified, and a domain that survives keeps its
        // verification — re-saving a connection must not silently re-verify anything.
        foreach (var removed in connection.Domains.Where(d => !domains.Contains(d.Domain)).ToList())
        {
            connection.Domains.Remove(removed);
            db.SsoDomains.Remove(removed);
        }

        foreach (var added in domains.Where(d => connection.Domains.All(existing => existing.Domain != d)))
        {
            connection.Domains.Add(new WorkspaceSsoDomain
            {
                ConnectionId = connection.Id,
                Domain = added,
                CreatedAt = now,
            });
        }
    }

    private OidcClientConfiguration BuildClientConfiguration(WorkspaceSsoConnection connection)
    {
        try
        {
            return new OidcClientConfiguration(connection.Issuer, connection.ClientId,
                secretProtector.Unprotect(connection.ClientSecretCiphertext));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SSO client secret could not be decrypted for workspace {WorkspaceId}",
                connection.WorkspaceId);
            throw new DomainException(ErrorCodes.SecretProtectionFailed,
                "The stored identity provider secret could not be read. Save the connection again.");
        }
    }

    private SsoConnectionResponse Project(WorkspaceSsoConnection connection) => new(
        connection.WorkspaceId,
        connection.Protocol.ToString().ToUpperInvariant(),
        connection.Issuer,
        connection.ClientId,
        connection.Enabled,
        connection.JitProvisioning,
        connection.DefaultRole.ToString().ToUpperInvariant(),
        connection.IsUsable,
        connection.Domains
            .OrderBy(d => d.Domain, StringComparer.Ordinal)
            .Select(d => new SsoDomainResponse(d.Domain, d.VerifiedAt is not null, d.VerifiedAt))
            .ToList(),
        _options.RedirectUri,
        connection.LastUsedAt,
        connection.UpdatedAt);

    private static DomainException SsoFailed() => new(ErrorCodes.SsoFailed,
        "We could not complete that sign-in. Try again, or sign in with a password.");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

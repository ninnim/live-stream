using System.ComponentModel.DataAnnotations;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Domain.Common;
using LiveStream.Domain.Governance;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Auth;

/// <summary>
/// Email/password authentication with rotating refresh tokens (docs/11-security.md).
/// Deliberately small: Phase 1 needs identity and workspace membership, and nothing else.
/// SSO/MFA are later phases and slot in behind the same contracts.
/// </summary>
public sealed class AuthService(
    IAppDbContext db,
    IOptions<GovernanceOptions> governance,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer tokenIssuer,
    IClock clock,
    ILogger<AuthService> logger)
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length is 0 or > 100)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Display name is required and must be 100 characters or fewer.");
        }

        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "An account with that email already exists.");
        }

        var now = clock.UtcNow;
        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHasher.Hash(request.Password),
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Every user gets a workspace so session ownership always has a tenant to hang from.
        var workspace = new Workspace
        {
            Name = string.IsNullOrWhiteSpace(request.WorkspaceName)
                ? $"{displayName}'s workspace"
                : request.WorkspaceName.Trim(),
            OwnerUserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var membership = new WorkspaceMember
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            CreatedAt = now,
        };

        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(membership);

        // The limits row is created with the workspace so a tenant's entitlements are never
        // implicit. A workspace with no row would fall back to the most restrictive plan.
        db.WorkspaceLimits.Add(WorkspaceLimits.DefaultFor(workspace.Id, governance.Value.DefaultPlan, now));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User registered {UserId} workspace={WorkspaceId}", user.Id, workspace.Id);

        return await IssueSessionAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // Same error for unknown account and wrong password so the endpoint cannot enumerate users.
        if (user is null)
        {
            throw new DomainException(ErrorCodes.AuthenticationFailed, "Email or password is incorrect.");
        }

        // An account created through single sign-on has no password. Failing here, with the same
        // message as a wrong one, keeps that from being a way to sign in without the provider.
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            throw new DomainException(ErrorCodes.AuthenticationFailed, "Email or password is incorrect.");
        }

        var (verified, needsRehash) = passwordHasher.Verify(user.PasswordHash, request.Password ?? string.Empty);
        if (!verified)
        {
            logger.LogWarning("Failed login attempt for user {UserId}", user.Id);
            throw new DomainException(ErrorCodes.AuthenticationFailed, "Email or password is incorrect.");
        }

        if (user.Status is UserStatus.Suspended)
        {
            throw new DomainException(ErrorCodes.PermissionDenied, "This account is suspended.");
        }

        if (needsRehash)
        {
            user.PasswordHash = passwordHasher.Hash(request.Password!);
            user.UpdatedAt = clock.UtcNow;
        }

        return await IssueSessionAsync(user, cancellationToken);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair. Presenting an already-rotated token revokes the
    /// whole chain, which is the standard defence against replay of a stolen token.
    /// </summary>
    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new DomainException(ErrorCodes.AuthenticationFailed, "Your session has expired. Please sign in again.");
        }

        var hash = RefreshToken.Hash(request.RefreshToken);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        var now = clock.UtcNow;

        if (token is null || token.User is null)
        {
            throw new DomainException(ErrorCodes.AuthenticationFailed, "Your session has expired. Please sign in again.");
        }

        if (!token.IsUsableAt(now))
        {
            if (token.ReplacedByTokenId is not null)
            {
                // A superseded token was presented: treat it as a stolen-token replay and kill the
                // whole chain. The revocations must be committed before the request fails, or the
                // detection would have no effect at all.
                await RevokeAllForUserAsync(token.UserId, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                logger.LogWarning("Refresh token replay detected; revoked all tokens for user {UserId}", token.UserId);
            }

            throw new DomainException(ErrorCodes.AuthenticationFailed, "Your session has expired. Please sign in again.");
        }

        var replacement = CreateRefreshToken(token.UserId, now, out var plaintext);
        token.RevokedAt = now;
        token.ReplacedByTokenId = replacement.Id;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);

        var (accessToken, expiresIn) = tokenIssuer.Issue(token.User.Id, token.User.Email);
        return new AuthResponse(accessToken, expiresIn, plaintext,
            await BuildCurrentUserAsync(token.User, cancellationToken));
    }

    public async Task LogoutAsync(Guid userId, string? refreshToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await RevokeAllForUserAsync(userId, now, cancellationToken);
        }
        else
        {
            var hash = RefreshToken.Hash(refreshToken);
            var token = await db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, cancellationToken);

            if (token is not null)
            {
                token.RevokedAt ??= now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CurrentUserResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new DomainException(ErrorCodes.AuthenticationFailed, "Account not found.");

        return await BuildCurrentUserAsync(user, cancellationToken);
    }

    /// <summary>
    /// Issues an access token and a fresh refresh token for a user whose identity has already been
    /// established. Public because single sign-on establishes identity elsewhere and then needs the
    /// same session; the token lifetimes and rotation rules must not be duplicated to do it.
    /// </summary>
    public async Task<AuthResponse> IssueSessionAsync(User user, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var refreshToken = CreateRefreshToken(user.Id, now, out var plaintext);
        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(cancellationToken);

        var (accessToken, expiresIn) = tokenIssuer.Issue(user.Id, user.Email);
        return new AuthResponse(accessToken, expiresIn, plaintext, await BuildCurrentUserAsync(user, cancellationToken));
    }

    private async Task<CurrentUserResponse> BuildCurrentUserAsync(User user, CancellationToken cancellationToken)
    {
        var memberships = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { m.Role, w.Id, w.Name, w.OwnerUserId })
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return new CurrentUserResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            memberships
                .Select(m => new WorkspaceMembershipResponse(m.Id, m.Name, m.Role.ToString().ToUpperInvariant(),
                    m.OwnerUserId == user.Id))
                .ToList());
    }

    private static RefreshToken CreateRefreshToken(Guid userId, DateTimeOffset now, out string plaintext)
    {
        plaintext = RefreshToken.GenerateToken();
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = RefreshToken.Hash(plaintext),
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshTokenLifetime),
        };
    }

    private async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }
    }

    private static string NormalizeEmail(string? email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.Length is 0 or > 320 || !new EmailAddressAttribute().IsValid(normalized))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A valid email address is required.");
        }

        return normalized;
    }

    private static void ValidatePassword(string? password)
    {
        if (password is null || password.Length < 12 || password.Length > 256)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Password must be between 12 and 256 characters.");
        }
    }
}

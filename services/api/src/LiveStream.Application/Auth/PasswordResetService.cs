using System.ComponentModel.DataAnnotations;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Auth;

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>
/// Recovering an account whose password is lost.
///
/// Three rules shape everything here, and each one costs something that would otherwise be nicer.
///
/// **It never says whether an account exists.** Forgetting a password is the one unauthenticated
/// endpoint that takes an email address and does different work depending on the answer, which
/// makes it the natural place to enumerate a platform's users. So the response is identical either
/// way, and the work either way takes a similar path.
///
/// **The token is stored hashed and is single-use.** A reset link is a credential that arrives by
/// email, sits in an inbox indefinitely, and gets forwarded by people who do not think of it as a
/// credential. See <see cref="PasswordResetToken"/>.
///
/// **Resetting revokes every session.** Somebody resets a password either because they forgot it
/// or because they think somebody else has it. The second case is the one that matters, and
/// leaving the intruder's refresh token alive would make the reset useless for exactly the person
/// who needed it most.
/// </summary>
public sealed class PasswordResetService(
    IAppDbContext db,
    IPasswordHasher passwordHasher,
    IEmailSender emailSender,
    IOptions<PasswordResetOptions> options,
    IClock clock,
    ILogger<PasswordResetService> logger)
{
    private readonly PasswordResetOptions _options = options.Value;

    /// <summary>
    /// Starts a reset.
    ///
    /// Returns nothing, and that is the point: the caller has nothing to report back except that
    /// the request was accepted. An unknown address, a suspended account, and a mail server that
    /// refused the message are all indistinguishable from success from outside.
    /// </summary>
    public async Task RequestAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmailOrNull(request.Email);

        if (email is null)
        {
            // A malformed address cannot belong to anybody, so there is nothing to do — and
            // nothing to say, for the same reason as below.
            logger.LogInformation("Password reset requested for a malformed address");
            return;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            // Logged, so an operator investigating a support call can see the attempt; answered
            // exactly as a real one is, so a stranger cannot tell a real address from a guess.
            logger.LogInformation("Password reset requested for an address with no active account");
            return;
        }

        var now = clock.UtcNow;

        // Asking again invalidates the previous link. Somebody who requests twice because the
        // first mail was slow should not end up with two live ways into their account.
        await InvalidateOutstandingAsync(user.Id, now, cancellationToken);

        var plaintext = PasswordResetToken.GenerateToken();

        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = PasswordResetToken.Hash(plaintext),
            CreatedAt = now,
            ExpiresAt = now.Add(PasswordResetToken.Lifetime),
        });

        await db.SaveChangesAsync(cancellationToken);

        // Sent after the token is committed. The other order would produce a link that does not
        // work yet, and a link that arrives before it is valid is worse than one that arrives late.
        var sent = await emailSender.SendAsync(BuildEmail(user, plaintext), cancellationToken);

        if (!sent)
        {
            // The token stands. The person can ask again, and the operator has a log line saying
            // why the first one never arrived — which is the actual fault to fix.
            logger.LogWarning("Password reset email could not be sent for user {UserId}", user.Id);
        }
    }

    /// <summary>
    /// Completes a reset. Throws only for a token that cannot be redeemed or a password that is
    /// not acceptable — both of which the person in front of the form can act on.
    /// </summary>
    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);

        var presented = (request.Token ?? string.Empty).Trim();

        if (presented.Length is 0 or > 512)
        {
            throw InvalidToken();
        }

        var hash = PasswordResetToken.Hash(presented);
        var now = clock.UtcNow;

        var token = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || !token.IsUsableAt(now) || token.User is null)
        {
            throw InvalidToken();
        }

        if (token.User.Status != UserStatus.Active)
        {
            // A suspended account must not be recoverable by whoever holds a link issued before
            // the suspension.
            throw InvalidToken();
        }

        token.User.PasswordHash = passwordHasher.Hash(request.NewPassword);
        token.User.UpdatedAt = now;
        token.UsedAt = now;

        // Any other outstanding link for this account dies with it: the password it was issued
        // against no longer exists.
        await InvalidateOutstandingAsync(token.UserId, now, cancellationToken);

        // Every session, not just other ones — there is no session here to keep. Somebody who
        // resets because they believe their account is compromised has to end up with the intruder
        // signed out, and the intruder's refresh token is the thing that would otherwise survive.
        var refreshTokens = await db.RefreshTokens
            .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Password reset completed for user {UserId}; {SessionCount} sessions revoked",
            token.UserId, refreshTokens.Count);
    }

    private async Task InvalidateOutstandingAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outstanding = await db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in outstanding)
        {
            token.InvalidatedAt = now;
        }
    }

    private EmailMessage BuildEmail(User user, string plaintextToken)
    {
        var link = $"{_options.WebAppBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(plaintextToken)}";
        var hours = (int)PasswordResetToken.Lifetime.TotalHours;
        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? "there" : user.DisplayName;

        // Both bodies say the same thing. A mail client that shows the plain part must not show
        // something less useful than the one that shows the HTML.
        var text =
            $"""
             Hi {name},

             Someone asked to reset the password for your {_options.ProductName} account.
             Open this link to choose a new one:

             {link}

             The link works once and expires in {hours} hour{(hours == 1 ? "" : "s")}.

             If this was not you, you can ignore this email — your password has not changed.
             """;

        var html =
            $"""
             <p>Hi {Escape(name)},</p>
             <p>Someone asked to reset the password for your {Escape(_options.ProductName)} account.
             Choose a new one here:</p>
             <p><a href="{Escape(link)}">Reset your password</a></p>
             <p>The link works once and expires in {hours} hour{(hours == 1 ? "" : "s")}.</p>
             <p>If this was not you, you can ignore this email — your password has not changed.</p>
             """;

        return new EmailMessage(user.Email, $"Reset your {_options.ProductName} password", text, html);
    }

    /// <summary>
    /// Minimal escaping for the few values that reach the HTML body: a display name the user chose,
    /// and a link this code built. Enough for those, and not a general-purpose sanitiser.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static DomainException InvalidToken() =>
        new(ErrorCodes.ResetTokenInvalid,
            "This reset link is no longer valid. Request a new one.");

    private static string? NormalizeEmailOrNull(string? email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();

        // Returns null rather than throwing, unlike the equivalent in AuthService: a validation
        // error here would answer "that is not an email address" to a probe, and answering
        // differently is the one thing this endpoint must not do.
        return normalized.Length is 0 or > 320 || !new EmailAddressAttribute().IsValid(normalized)
            ? null
            : normalized;
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

public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    /// <summary>What the emails call this platform.</summary>
    [Required]
    public string ProductName { get; set; } = "Live Studio";

    /// <summary>
    /// Where a reset link points. Defaults to the same origin join links use, rather than being a
    /// second place to configure the web app's address.
    /// </summary>
    [Required]
    public string WebAppBaseUrl { get; set; } = "http://localhost:3000";
}

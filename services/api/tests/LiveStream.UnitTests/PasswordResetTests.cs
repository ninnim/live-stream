using LiveStream.Application.Auth;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Infrastructure.Auth;
using LiveStream.Infrastructure.Persistence;
using LiveStream.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Recovering a lost password.
///
/// Most of these test a refusal rather than a feature. This is the one unauthenticated endpoint
/// that takes an email address and does different work depending on whether it belongs to somebody,
/// which makes every difference in its behaviour a way to enumerate the platform's users — so what
/// it declines to reveal is the substance of the design.
/// </summary>
public sealed class PasswordResetTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeEmailSender _email = new();
    private readonly AspNetPasswordHasher _hasher = new();

    private AppDbContext NewContext() => _database.CreateContext();

    private PasswordResetService NewService(AppDbContext context) =>
        new(context, _hasher, _email,
            Options.Create(new PasswordResetOptions { WebAppBaseUrl = "https://studio.test" }),
            _clock,
            NullLogger<PasswordResetService>.Instance);

    private async Task<User> SeedUserAsync(
        string email = "creator@example.com",
        UserStatus status = UserStatus.Active)
    {
        await using var context = NewContext();

        var user = new User
        {
            Email = email,
            DisplayName = "Creator",
            PasswordHash = _hasher.Hash("the-original-password"),
            Status = status,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    /// <summary>Pulls the token out of the email, which is the only place it ever exists as plaintext.</summary>
    private string TokenFromEmail()
    {
        var link = _email.Last!.TextBody
            .Split('\n', StringSplitOptions.TrimEntries)
            .First(line => line.StartsWith("https://studio.test/reset-password", StringComparison.Ordinal));

        return Uri.UnescapeDataString(link.Split("token=")[1]);
    }

    // -----------------------------------------------------------------------------------------
    // Requesting a reset
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_a_reset_emails_a_working_link()
    {
        var user = await SeedUserAsync();

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        Assert.Single(_email.Sent);
        Assert.Equal(user.Email, _email.Last!.ToAddress);
        Assert.Contains("https://studio.test/reset-password?token=", _email.Last.TextBody, StringComparison.Ordinal);

        // Both bodies carry it: a mail client showing the plain part must not show something less
        // useful than one showing the HTML.
        Assert.Contains("https://studio.test/reset-password?token=", _email.Last.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_is_never_stored_in_a_form_that_could_be_used()
    {
        // The reason this is a table of hashes rather than a column on the user. A stolen database
        // backup must not contain a way into anybody's account.
        var user = await SeedUserAsync();

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        var plaintext = TokenFromEmail();

        await using var verify = NewContext();
        var stored = await verify.PasswordResetTokens.SingleAsync();

        Assert.NotEqual(plaintext, stored.TokenHash);
        Assert.Equal(PasswordResetToken.Hash(plaintext), stored.TokenHash);
    }

    [Fact]
    public async Task An_unknown_address_is_answered_exactly_like_a_known_one()
    {
        // No throw, no email, no row — and, crucially, nothing the caller can tell apart from the
        // success case, because the endpoint returns the same 202 either way.
        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest("stranger@example.com"), default);
        }

        Assert.Empty(_email.Sent);

        await using var verify = NewContext();
        Assert.Empty(await verify.PasswordResetTokens.ToListAsync());
    }

    [Fact]
    public async Task A_malformed_address_is_answered_the_same_way_too()
    {
        // Validating it would answer "that is not an email address", which is a different answer,
        // which is the one thing this endpoint must never give.
        await using var context = NewContext();

        await NewService(context).RequestAsync(new ForgotPasswordRequest("not-an-address"), default);

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task A_suspended_account_cannot_be_recovered()
    {
        var user = await SeedUserAsync(status: UserStatus.Suspended);

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Asking_twice_kills_the_first_link()
    {
        // Somebody who asks again because the first mail was slow must not end up with two live
        // ways into their account.
        var user = await SeedUserAsync();

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        var first = TokenFromEmail();

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        var second = TokenFromEmail();
        Assert.NotEqual(first, second);

        await using (var context = NewContext())
        {
            await Assert.ThrowsAsync<DomainException>(() =>
                NewService(context).ResetAsync(new ResetPasswordRequest(first, "a-brand-new-password"), default));
        }

        // The newest one still works, so the person is not locked out by their own impatience.
        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(second, "a-brand-new-password"), default);
        }
    }

    [Fact]
    public async Task A_mail_server_that_refuses_the_message_does_not_change_the_answer()
    {
        // The token stands and nothing is thrown: the person can ask again, and the operator has a
        // log line naming the real fault.
        _email.FailSends = true;
        var user = await SeedUserAsync();

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        }

        await using var verify = NewContext();
        Assert.Single(await verify.PasswordResetTokens.ToListAsync());
    }

    [Fact]
    public async Task The_address_is_matched_regardless_of_how_it_was_typed()
    {
        var user = await SeedUserAsync("creator@example.com");

        await using (var context = NewContext())
        {
            await NewService(context).RequestAsync(new ForgotPasswordRequest("  CREATOR@Example.COM "), default);
        }

        Assert.Single(_email.Sent);
        Assert.Equal(user.Email, _email.Last!.ToAddress);
    }

    // -----------------------------------------------------------------------------------------
    // Redeeming one
    // -----------------------------------------------------------------------------------------

    private async Task<string> RequestTokenAsync(User user)
    {
        await using var context = NewContext();
        await NewService(context).RequestAsync(new ForgotPasswordRequest(user.Email), default);
        return TokenFromEmail();
    }

    [Fact]
    public async Task Resetting_sets_the_new_password()
    {
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
        }

        await using var verify = NewContext();
        var updated = await verify.Users.SingleAsync();

        Assert.True(_hasher.Verify(updated.PasswordHash, "a-brand-new-password").Verified);
        Assert.False(_hasher.Verify(updated.PasswordHash, "the-original-password").Verified);
    }

    [Fact]
    public async Task A_link_only_works_once()
    {
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
        }

        await using (var context = NewContext())
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                NewService(context).ResetAsync(new ResetPasswordRequest(token, "another-new-password"), default));

            Assert.Equal(ErrorCodes.ResetTokenInvalid, error.ErrorCode);
        }
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        _clock.Advance(PasswordResetToken.Lifetime + TimeSpan.FromMinutes(1));

        await using var context = NewContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default));

        Assert.Equal(ErrorCodes.ResetTokenInvalid, error.ErrorCode);
    }

    [Fact]
    public async Task A_link_a_minute_before_expiry_still_works()
    {
        // The boundary in the direction that matters: expiring early would lock people out of a
        // link they were told they had an hour to use.
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        _clock.Advance(PasswordResetToken.Lifetime - TimeSpan.FromMinutes(1));

        await using var context = NewContext();
        await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task A_token_that_was_never_issued_is_refused_the_same_way(string presented)
    {
        // One code for expired, used and never-valid. Telling them apart would let somebody
        // holding a stolen link learn whether it had already been used.
        await SeedUserAsync();

        await using var context = NewContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            NewService(context).ResetAsync(new ResetPasswordRequest(presented, "a-brand-new-password"), default));

        Assert.Equal(ErrorCodes.ResetTokenInvalid, error.ErrorCode);
    }

    [Fact]
    public async Task Resetting_signs_every_session_out()
    {
        // The case this is for: somebody resets because they believe an intruder has their
        // password. Leaving the intruder's refresh token alive would make the reset pointless for
        // exactly the person who needed it most.
        var user = await SeedUserAsync();

        await using (var seed = NewContext())
        {
            seed.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = RefreshToken.Hash("the-intruders-session"),
                CreatedAt = _clock.UtcNow,
                ExpiresAt = _clock.UtcNow.AddDays(14),
            });

            await seed.SaveChangesAsync();
        }

        var token = await RequestTokenAsync(user);

        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
        }

        await using var verify = NewContext();
        Assert.All(await verify.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task A_short_password_is_refused_before_the_token_is_spent()
    {
        // Otherwise a typo in the new password would burn the only link the person has.
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        await using (var context = NewContext())
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                NewService(context).ResetAsync(new ResetPasswordRequest(token, "short"), default));

            Assert.Equal(ErrorCodes.ValidationFailed, error.ErrorCode);
        }

        // Still usable, because nothing was consumed.
        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
        }
    }

    [Fact]
    public async Task An_account_suspended_after_the_link_was_issued_cannot_be_recovered_with_it()
    {
        var user = await SeedUserAsync();
        var token = await RequestTokenAsync(user);

        await using (var suspend = NewContext())
        {
            var stored = await suspend.Users.SingleAsync();
            stored.Status = UserStatus.Suspended;
            await suspend.SaveChangesAsync();
        }

        await using var context = NewContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default));

        Assert.Equal(ErrorCodes.ResetTokenInvalid, error.ErrorCode);
    }

    [Fact]
    public async Task One_person_s_link_cannot_reset_another_person_s_password()
    {
        var first = await SeedUserAsync("first@example.com");
        var second = await SeedUserAsync("second@example.com");

        var token = await RequestTokenAsync(first);

        await using (var context = NewContext())
        {
            await NewService(context).ResetAsync(new ResetPasswordRequest(token, "a-brand-new-password"), default);
        }

        await using var verify = NewContext();
        var other = await verify.Users.SingleAsync(u => u.Id == second.Id);

        Assert.True(_hasher.Verify(other.PasswordHash, "the-original-password").Verified);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();
}

using LiveStream.Domain.Identity;
using LiveStream.Domain.Media;
using LiveStream.Infrastructure.Auth;
using Xunit;

namespace LiveStream.UnitTests;

public class WorkspacePermissionTests
{
    [Theory]
    [InlineData(WorkspaceRole.Owner, WorkspacePermission.LiveSessionStart, true)]
    [InlineData(WorkspaceRole.Admin, WorkspacePermission.LiveSessionStop, true)]
    [InlineData(WorkspaceRole.Producer, WorkspacePermission.LiveSessionStart, true)]
    [InlineData(WorkspaceRole.Host, WorkspacePermission.LiveSessionStart, true)]
    [InlineData(WorkspaceRole.Moderator, WorkspacePermission.LiveSessionStart, false)]
    [InlineData(WorkspaceRole.Analyst, WorkspacePermission.LiveSessionStart, false)]
    [InlineData(WorkspaceRole.Viewer, WorkspacePermission.LiveSessionStart, false)]
    public void Only_broadcasting_roles_may_start_a_session(WorkspaceRole role, WorkspacePermission permission,
        bool expected)
    {
        Assert.Equal(expected, WorkspacePermissions.Allows(role, permission));
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Moderator)]
    public void Read_only_roles_may_still_view_sessions(WorkspaceRole role)
    {
        Assert.True(WorkspacePermissions.Allows(role, WorkspacePermission.LiveSessionView));
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Moderator)]
    [InlineData(WorkspaceRole.Analyst)]
    public void Read_only_roles_cannot_edit_sessions(WorkspaceRole role)
    {
        Assert.False(WorkspacePermissions.Allows(role, WorkspacePermission.LiveSessionEdit));
    }

    [Fact]
    public void Every_role_has_an_explicit_grant_set()
    {
        // A role with no mapping would silently deny everything; make that impossible to miss.
        foreach (var role in Enum.GetValues<WorkspaceRole>())
        {
            Assert.NotEmpty(WorkspacePermissions.For(role));
        }
    }
}

public class IngestCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_returns_a_token_that_is_never_stored_in_plaintext()
    {
        var (credential, token) = IngestCredential.Issue(Guid.NewGuid(), Guid.NewGuid(), "ls_abc",
            IngestCredentialScope.Publish, Now, TimeSpan.FromMinutes(5));

        Assert.NotEmpty(token);
        Assert.NotEqual(token, credential.TokenHash);
        Assert.Equal(IngestCredential.HashToken(token), credential.TokenHash);
        Assert.Equal(64, credential.TokenHash.Length); // SHA-256, hex encoded.
    }

    [Fact]
    public void Issued_tokens_are_unique()
    {
        var tokens = Enumerable.Range(0, 50)
            .Select(_ => IngestCredential.Issue(Guid.NewGuid(), Guid.NewGuid(), "ls_abc",
                IngestCredentialScope.Publish, Now, TimeSpan.FromMinutes(5)).PlaintextToken)
            .ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void A_credential_stops_being_usable_after_its_lifetime()
    {
        var (credential, _) = IngestCredential.Issue(Guid.NewGuid(), Guid.NewGuid(), "ls_abc",
            IngestCredentialScope.Publish, Now, TimeSpan.FromMinutes(5));

        Assert.True(credential.IsUsableAt(Now.AddMinutes(4)));
        Assert.False(credential.IsUsableAt(Now.AddMinutes(5)));
        Assert.False(credential.IsUsableAt(Now.AddMinutes(6)));
    }

    [Fact]
    public void Revocation_is_immediate_and_idempotent()
    {
        var (credential, _) = IngestCredential.Issue(Guid.NewGuid(), Guid.NewGuid(), "ls_abc",
            IngestCredentialScope.Publish, Now, TimeSpan.FromMinutes(5));

        credential.Revoke(Now.AddMinutes(1));
        var firstRevokedAt = credential.RevokedAt;

        credential.Revoke(Now.AddMinutes(2));

        Assert.Equal(firstRevokedAt, credential.RevokedAt);
        Assert.False(credential.IsUsableAt(Now.AddMinutes(1)));
    }

    [Fact]
    public void MarkUsed_records_only_the_first_use()
    {
        var (credential, _) = IngestCredential.Issue(Guid.NewGuid(), Guid.NewGuid(), "ls_abc",
            IngestCredentialScope.Publish, Now, TimeSpan.FromMinutes(5));

        credential.MarkUsed(Now.AddSeconds(10));
        credential.MarkUsed(Now.AddSeconds(20));

        Assert.Equal(Now.AddSeconds(10), credential.FirstUsedAt);
    }

    [Fact]
    public void Hashing_is_deterministic_and_distinguishes_tokens()
    {
        Assert.Equal(IngestCredential.HashToken("abc"), IngestCredential.HashToken("abc"));
        Assert.NotEqual(IngestCredential.HashToken("abc"), IngestCredential.HashToken("abd"));
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void A_correct_password_verifies()
    {
        var hasher = new AspNetPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        var (verified, _) = hasher.Verify(hash, "correct horse battery staple");
        Assert.True(verified);
    }

    [Fact]
    public void An_incorrect_password_does_not_verify()
    {
        var hasher = new AspNetPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        var (verified, _) = hasher.Verify(hash, "wrong password entirely");
        Assert.False(verified);
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        var hasher = new AspNetPasswordHasher();

        // Distinct salts, so identical passwords are not identifiable from the stored hashes.
        Assert.NotEqual(hasher.Hash("same password here"), hasher.Hash("same password here"));
    }
}

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generated_tokens_are_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => RefreshToken.GenerateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, token =>
            Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')));
    }

    [Fact]
    public void An_expired_or_revoked_token_is_not_usable()
    {
        var token = new RefreshToken { ExpiresAt = Now.AddDays(1), CreatedAt = Now };

        Assert.True(token.IsUsableAt(Now));
        Assert.False(token.IsUsableAt(Now.AddDays(2)));

        token.RevokedAt = Now;
        Assert.False(token.IsUsableAt(Now));
    }
}

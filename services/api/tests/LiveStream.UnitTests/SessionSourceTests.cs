using LiveStream.Domain.Common;
using LiveStream.Domain.Sources;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Exhaustive, matching the session and destination machines. The legal set is written out
/// independently of the implementation so the test cannot agree with a mistake by construction.
/// </summary>
public class SourceStateMachineTests
{
    private static readonly (SourceStatus From, SourceStatus To)[] LegalTransitions =
    [
        (SourceStatus.Invited, SourceStatus.Paired),
        (SourceStatus.Invited, SourceStatus.Revoked),

        (SourceStatus.Paired, SourceStatus.Connected),
        (SourceStatus.Paired, SourceStatus.Disconnected),
        (SourceStatus.Paired, SourceStatus.Revoked),

        (SourceStatus.Connected, SourceStatus.Disconnected),
        (SourceStatus.Connected, SourceStatus.Revoked),

        (SourceStatus.Disconnected, SourceStatus.Connected),
        (SourceStatus.Disconnected, SourceStatus.Revoked),
    ];

    [Fact]
    public void Every_declared_transition_is_allowed()
    {
        foreach (var (from, to) in LegalTransitions)
        {
            Assert.True(SourceStateMachine.CanTransition(from, to), $"{from} -> {to} should be legal.");
        }
    }

    [Fact]
    public void Every_other_transition_is_rejected()
    {
        var legal = LegalTransitions.ToHashSet();

        foreach (var from in Enum.GetValues<SourceStatus>())
        {
            foreach (var to in Enum.GetValues<SourceStatus>())
            {
                if (from == to || legal.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(SourceStateMachine.CanTransition(from, to), $"{from} -> {to} should be illegal.");
            }
        }
    }

    /// <summary>
    /// "Revocation is immediate" also means it is final: a way back would let a withdrawn device
    /// return without a new code.
    /// </summary>
    [Fact]
    public void Revoked_is_terminal()
    {
        Assert.True(SourceStateMachine.IsTerminal(SourceStatus.Revoked));
        Assert.Empty(SourceStateMachine.AllowedTargets(SourceStatus.Revoked));
    }

    /// <summary>A phone in a tunnel has not left the show.</summary>
    [Fact]
    public void A_disconnected_source_can_come_back()
    {
        Assert.True(SourceStateMachine.CanTransition(SourceStatus.Disconnected, SourceStatus.Connected));
        Assert.True(SourceStateMachine.IsActive(SourceStatus.Disconnected));
    }

    [Fact]
    public void An_invited_source_cannot_jump_straight_to_connected() =>
        Assert.False(SourceStateMachine.CanTransition(SourceStatus.Invited, SourceStatus.Connected));
}

public class SessionSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid Operator = Guid.NewGuid();
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private static (SessionSource Source, string Code) Invite(SourceRole role = SourceRole.Camera) =>
        SessionSource.Invite(SessionId, role, "Phone camera", "ls_source_path", Operator, Now, CodeLifetime);

    // -----------------------------------------------------------------------------------------
    // Invitation
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void An_invited_source_starts_invited_with_a_code()
    {
        var (source, code) = Invite();

        Assert.Equal(SourceStatus.Invited, source.Status);
        Assert.Equal(SourceRole.Camera, source.Role);
        Assert.NotNull(source.PairingCodeHash);
        Assert.Equal(Now.Add(CodeLifetime), source.PairingCodeExpiresAt);
        Assert.Null(source.DeviceTokenHash);
        Assert.Equal(8, code.Length);
    }

    /// <summary>The code is stored only as a hash; the plaintext exists once and is never persisted.</summary>
    [Fact]
    public void The_pairing_code_is_not_stored_in_the_clear()
    {
        var (source, code) = Invite();

        Assert.NotEqual(code, source.PairingCodeHash);
        Assert.Equal(SessionSource.HashSecret(code), source.PairingCodeHash);
    }

    /// <summary>Someone is typing this on a phone, so ambiguous characters are excluded.</summary>
    [Fact]
    public void Codes_avoid_characters_that_are_easily_confused()
    {
        for (var i = 0; i < 200; i++)
        {
            var (_, code) = Invite();
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I' or 'L' or 'U');
        }
    }

    [Fact]
    public void A_session_cannot_be_given_a_second_host()
    {
        var exception = Assert.Throws<DomainException>(() => Invite(SourceRole.Host));
        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    /// <summary>Roles that send no media get no ingest path rather than an unused one.</summary>
    [Theory]
    [InlineData(SourceRole.Moderator)]
    [InlineData(SourceRole.Operator)]
    [InlineData(SourceRole.ViewerMonitor)]
    public void Non_media_roles_get_no_ingest_path(SourceRole role)
    {
        var (source, _) = Invite(role);
        Assert.Equal(string.Empty, source.MediaPathName);
    }

    [Theory]
    [InlineData(SourceRole.Camera)]
    [InlineData(SourceRole.Screen)]
    [InlineData(SourceRole.Audio)]
    public void Media_roles_get_their_own_ingest_path(SourceRole role)
    {
        var (source, _) = Invite(role);
        Assert.Equal("ls_source_path", source.MediaPathName);
    }

    // -----------------------------------------------------------------------------------------
    // Claiming
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Claiming_issues_a_token_and_pairs_the_source()
    {
        var (source, _) = Invite();

        var token = source.Claim(Now.AddMinutes(1), TokenLifetime, "Alice's iPhone");

        Assert.Equal(SourceStatus.Paired, source.Status);
        Assert.NotEmpty(token);
        Assert.Equal(SessionSource.HashSecret(token), source.DeviceTokenHash);
        Assert.True(source.IsDeviceTokenUsableAt(Now.AddMinutes(1)));
    }

    /// <summary>
    /// A device describing itself is useful in the audit trail, but must not overwrite the name the
    /// operator chose — a "Stage left camera" that silently becomes "Windows PC" is worse than no
    /// label at all.
    /// </summary>
    [Fact]
    public void A_device_cannot_rename_itself_over_the_operators_choice()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime, "Windows PC");

        Assert.Equal("Phone camera", source.DisplayName);

        var paired = source.Events.Last(e => e.Type == SourceEventType.Paired);
        Assert.Contains("Windows PC", paired.Detail);
    }

    /// <summary>
    /// The code is erased, not flagged. A redeemed code must be unusable even if the status check
    /// were ever bypassed.
    /// </summary>
    [Fact]
    public void Claiming_spends_the_code()
    {
        var (source, code) = Invite();
        source.Claim(Now, TokenLifetime);

        Assert.Null(source.PairingCodeHash);
        Assert.Null(source.PairingCodeExpiresAt);
        Assert.False(source.MatchesPairingCode(code));
    }

    [Fact]
    public void A_code_cannot_be_claimed_twice()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);

        var exception = Assert.Throws<DomainException>(() => source.Claim(Now, TokenLifetime));
        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    [Fact]
    public void An_expired_code_cannot_be_claimed()
    {
        var (source, _) = Invite();

        var exception = Assert.Throws<DomainException>(() =>
            source.Claim(Now.Add(CodeLifetime).AddSeconds(1), TokenLifetime));

        Assert.Equal(ErrorCodes.CredentialExpired, exception.ErrorCode);
        Assert.Equal(SourceStatus.Invited, source.Status);
    }

    [Fact]
    public void A_device_token_expires()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);

        Assert.True(source.IsDeviceTokenUsableAt(Now.Add(TokenLifetime).AddSeconds(-1)));
        Assert.False(source.IsDeviceTokenUsableAt(Now.Add(TokenLifetime)));
    }

    /// <summary>Codes are read off a screen, so separators and case must not matter.</summary>
    [Theory]
    [InlineData("ABCD-EFGH", "ABCDEFGH")]
    [InlineData("abcd efgh", "ABCDEFGH")]
    [InlineData("  abcdefgh  ", "ABCDEFGH")]
    [InlineData("AB-CD-EF-GH", "ABCDEFGH")]
    public void Codes_are_normalized_before_matching(string typed, string expected) =>
        Assert.Equal(expected, SessionSource.NormalizeCode(typed));

    [Fact]
    public void A_code_matches_however_it_was_typed()
    {
        var (source, code) = Invite();
        var formatted = SessionSource.FormatCode(code).ToLowerInvariant();

        Assert.True(source.MatchesPairingCode(formatted));
    }

    // -----------------------------------------------------------------------------------------
    // Revocation
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The central Phase 4 security property. Revocation erases both secrets rather than flagging
    /// them, so nothing usable survives even in the stored row.
    /// </summary>
    [Fact]
    public void Revoking_erases_every_credential_immediately()
    {
        var (source, _) = Invite();
        var token = source.Claim(Now, TokenLifetime);
        source.ObserveMedia(true, 2500, 1000, Now);

        source.Revoke(Now.AddMinutes(5), Operator, "Revoked by operator.");

        Assert.Equal(SourceStatus.Revoked, source.Status);
        Assert.Null(source.DeviceTokenHash);
        Assert.Null(source.DeviceTokenExpiresAt);
        Assert.Null(source.PairingCodeHash);
        Assert.False(source.IsDeviceTokenUsableAt(Now.AddMinutes(5)));
        Assert.False(source.MatchesDeviceToken(token));
        Assert.False(source.IngestConnected);
        Assert.False(source.IsProgram);
    }

    [Fact]
    public void Revoking_twice_is_harmless()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);

        source.Revoke(Now, Operator, "first");
        source.Revoke(Now.AddMinutes(1), Operator, "second");

        Assert.Equal(SourceStatus.Revoked, source.Status);
    }

    [Fact]
    public void A_revoked_source_cannot_be_claimed_again()
    {
        var (source, _) = Invite();
        source.Revoke(Now, Operator, "revoked");

        Assert.Throws<DomainException>(() => source.Claim(Now, TokenLifetime));
    }

    // -----------------------------------------------------------------------------------------
    // Presence
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Observing_media_moves_a_paired_source_to_connected()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);

        source.ObserveMedia(true, 2500, 50_000, Now.AddSeconds(5));

        Assert.Equal(SourceStatus.Connected, source.Status);
        Assert.True(source.IngestConnected);
        Assert.Equal(2500, source.BitrateKbps);
        Assert.Equal(50_000, source.BytesReceived);
        Assert.Equal(Now.AddSeconds(5), source.LastSeenAt);
    }

    [Fact]
    public void Losing_media_moves_a_connected_source_to_disconnected()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);
        source.ObserveMedia(true, 2500, 50_000, Now);

        source.ObserveMedia(false, null, 50_000, Now.AddSeconds(30));

        Assert.Equal(SourceStatus.Disconnected, source.Status);
        Assert.False(source.IngestConnected);
        Assert.Null(source.BitrateKbps);

        // The high-water mark of bytes survives, so a reconnect does not look like data loss.
        Assert.Equal(50_000, source.BytesReceived);
    }

    [Fact]
    public void Repeated_identical_observations_do_not_append_events()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);
        source.ObserveMedia(true, 2500, 1000, Now);

        var eventCount = source.Events.Count;
        source.ObserveMedia(true, 2600, 2000, Now.AddSeconds(3));

        Assert.Equal(eventCount, source.Events.Count);
    }

    // -----------------------------------------------------------------------------------------
    // Program
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Only_a_connected_media_source_can_go_on_air()
    {
        var (source, _) = Invite();
        source.Claim(Now, TokenLifetime);

        Assert.Throws<DomainException>(() => source.PromoteToProgram(Now, Operator));

        source.ObserveMedia(true, 2500, 1000, Now);
        source.PromoteToProgram(Now, Operator);

        Assert.True(source.IsProgram);
    }

    [Fact]
    public void A_role_that_sends_no_media_cannot_go_on_air()
    {
        var (source, _) = Invite(SourceRole.Moderator);
        source.Claim(Now, TokenLifetime);

        var exception = Assert.Throws<DomainException>(() => source.PromoteToProgram(Now, Operator));
        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    // -----------------------------------------------------------------------------------------
    // Role permissions
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A borrowed phone must not be able to cut the show or revoke the operator who lent it.
    /// </summary>
    [Theory]
    [InlineData(SourceRole.Camera)]
    [InlineData(SourceRole.Screen)]
    [InlineData(SourceRole.Audio)]
    public void Contributing_devices_cannot_switch_program_or_manage_devices(SourceRole role)
    {
        Assert.True(SourcePermissions.Allows(role, SourcePermission.PublishMedia));
        Assert.False(SourcePermissions.Allows(role, SourcePermission.SwitchProgram));
        Assert.False(SourcePermissions.Allows(role, SourcePermission.ManageDevices));
    }

    [Theory]
    [InlineData(SourceRole.Moderator)]
    [InlineData(SourceRole.Operator)]
    [InlineData(SourceRole.ViewerMonitor)]
    public void Non_contributing_roles_cannot_publish(SourceRole role)
    {
        Assert.False(SourcePermissions.Allows(role, SourcePermission.PublishMedia));
        Assert.False(SourcePermissions.ContributesMedia(role));
    }

    [Fact]
    public void Operators_can_run_the_show_but_not_appear_in_it()
    {
        Assert.True(SourcePermissions.Allows(SourceRole.Operator, SourcePermission.SwitchProgram));
        Assert.True(SourcePermissions.Allows(SourceRole.Operator, SourcePermission.ManageDevices));
        Assert.False(SourcePermissions.Allows(SourceRole.Operator, SourcePermission.PublishMedia));
    }

    [Fact]
    public void Every_role_can_at_least_see_the_session()
    {
        foreach (var role in Enum.GetValues<SourceRole>())
        {
            Assert.True(SourcePermissions.Allows(role, SourcePermission.ViewSession), $"{role} should see the session.");
        }
    }
}

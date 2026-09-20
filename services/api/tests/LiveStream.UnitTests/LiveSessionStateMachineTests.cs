using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// The state machine is the safety boundary for the whole product, so it is tested exhaustively:
/// every legal transition is asserted, and every remaining pair is asserted illegal.
/// </summary>
public class LiveSessionStateMachineTests
{
    /// <summary>The complete set of legal transitions, written out independently of the implementation.</summary>
    private static readonly (LiveSessionStatus From, LiveSessionStatus To)[] LegalTransitions =
    [
        (LiveSessionStatus.Draft, LiveSessionStatus.Preparing),
        (LiveSessionStatus.Draft, LiveSessionStatus.Ended),

        (LiveSessionStatus.Preparing, LiveSessionStatus.Ready),
        (LiveSessionStatus.Preparing, LiveSessionStatus.Failed),
        (LiveSessionStatus.Preparing, LiveSessionStatus.Ended),

        (LiveSessionStatus.Ready, LiveSessionStatus.Starting),
        (LiveSessionStatus.Ready, LiveSessionStatus.Preparing),
        (LiveSessionStatus.Ready, LiveSessionStatus.Failed),
        (LiveSessionStatus.Ready, LiveSessionStatus.Ended),

        (LiveSessionStatus.Starting, LiveSessionStatus.Live),
        (LiveSessionStatus.Starting, LiveSessionStatus.Failed),
        (LiveSessionStatus.Starting, LiveSessionStatus.Stopping),

        (LiveSessionStatus.Live, LiveSessionStatus.Degraded),
        (LiveSessionStatus.Live, LiveSessionStatus.Reconnecting),
        (LiveSessionStatus.Live, LiveSessionStatus.Stopping),
        (LiveSessionStatus.Live, LiveSessionStatus.Failed),

        (LiveSessionStatus.Degraded, LiveSessionStatus.Live),
        (LiveSessionStatus.Degraded, LiveSessionStatus.Reconnecting),
        (LiveSessionStatus.Degraded, LiveSessionStatus.Stopping),
        (LiveSessionStatus.Degraded, LiveSessionStatus.Failed),

        (LiveSessionStatus.Reconnecting, LiveSessionStatus.Live),
        (LiveSessionStatus.Reconnecting, LiveSessionStatus.Degraded),
        (LiveSessionStatus.Reconnecting, LiveSessionStatus.Stopping),
        (LiveSessionStatus.Reconnecting, LiveSessionStatus.Failed),

        (LiveSessionStatus.Stopping, LiveSessionStatus.Ended),
        (LiveSessionStatus.Stopping, LiveSessionStatus.Failed),
    ];

    public static TheoryData<LiveSessionStatus, LiveSessionStatus> LegalTransitionData()
    {
        var data = new TheoryData<LiveSessionStatus, LiveSessionStatus>();
        foreach (var (from, to) in LegalTransitions)
        {
            data.Add(from, to);
        }

        return data;
    }

    public static TheoryData<LiveSessionStatus, LiveSessionStatus> IllegalTransitionData()
    {
        var legal = LegalTransitions.ToHashSet();
        var data = new TheoryData<LiveSessionStatus, LiveSessionStatus>();

        foreach (var from in Enum.GetValues<LiveSessionStatus>())
        {
            foreach (var to in Enum.GetValues<LiveSessionStatus>())
            {
                if (from != to && !legal.Contains((from, to)))
                {
                    data.Add(from, to);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LegalTransitionData))]
    public void CanTransition_allows_every_documented_transition(LiveSessionStatus from, LiveSessionStatus to)
    {
        Assert.True(LiveSessionStateMachine.CanTransition(from, to),
            $"Expected {from} -> {to} to be allowed.");
    }

    [Theory]
    [MemberData(nameof(IllegalTransitionData))]
    public void CanTransition_rejects_everything_else(LiveSessionStatus from, LiveSessionStatus to)
    {
        Assert.False(LiveSessionStateMachine.CanTransition(from, to),
            $"Expected {from} -> {to} to be rejected.");
    }

    [Theory]
    [InlineData(LiveSessionStatus.Ended)]
    [InlineData(LiveSessionStatus.Failed)]
    public void Terminal_states_allow_no_further_transitions(LiveSessionStatus terminal)
    {
        Assert.True(LiveSessionStateMachine.IsTerminal(terminal));
        Assert.Empty(LiveSessionStateMachine.AllowedTargets(terminal));
    }

    [Fact]
    public void Live_cannot_go_straight_to_ended_without_stopping()
    {
        // Stopping is what triggers media teardown and recording finalization, so skipping it
        // would silently orphan both.
        Assert.False(LiveSessionStateMachine.CanTransition(LiveSessionStatus.Live, LiveSessionStatus.Ended));
    }

    [Fact]
    public void Reconnecting_is_reachable_from_live_and_can_return_to_live()
    {
        Assert.True(LiveSessionStateMachine.CanTransition(LiveSessionStatus.Live, LiveSessionStatus.Reconnecting));
        Assert.True(LiveSessionStateMachine.CanTransition(LiveSessionStatus.Reconnecting, LiveSessionStatus.Live));
    }

    [Fact]
    public void Reconnecting_never_becomes_ended_directly()
    {
        // A dropped connection must not silently end someone's broadcast (MASTER_BLUEPRINT.md §11.3).
        Assert.False(LiveSessionStateMachine.CanTransition(LiveSessionStatus.Reconnecting, LiveSessionStatus.Ended));
    }

    [Theory]
    [InlineData(LiveSessionStatus.Live, true)]
    [InlineData(LiveSessionStatus.Degraded, true)]
    [InlineData(LiveSessionStatus.Reconnecting, true)]
    [InlineData(LiveSessionStatus.Ready, false)]
    [InlineData(LiveSessionStatus.Starting, false)]
    [InlineData(LiveSessionStatus.Ended, false)]
    public void IsBroadcasting_identifies_states_carrying_media(LiveSessionStatus status, bool expected)
    {
        Assert.Equal(expected, LiveSessionStateMachine.IsBroadcasting(status));
    }

    [Theory]
    [InlineData(LiveSessionStatus.Ready, true)]
    [InlineData(LiveSessionStatus.Starting, true)]
    [InlineData(LiveSessionStatus.Live, true)]
    [InlineData(LiveSessionStatus.Degraded, true)]
    [InlineData(LiveSessionStatus.Reconnecting, true)]
    [InlineData(LiveSessionStatus.Draft, false)]
    [InlineData(LiveSessionStatus.Stopping, false)]
    [InlineData(LiveSessionStatus.Ended, false)]
    [InlineData(LiveSessionStatus.Failed, false)]
    public void ExpectsIngest_gates_when_credentials_are_useful(LiveSessionStatus status, bool expected)
    {
        Assert.Equal(expected, LiveSessionStateMachine.ExpectsIngest(status));
    }

    [Fact]
    public void EnsureCanTransition_throws_with_both_states_named()
    {
        var exception = Assert.Throws<InvalidStateTransitionException>(() =>
            LiveSessionStateMachine.EnsureCanTransition(LiveSessionStatus.Ended, LiveSessionStatus.Live));

        Assert.Equal(ErrorCodes.InvalidStateTransition, exception.ErrorCode);
        Assert.Equal("Ended", exception.From);
        Assert.Equal("Live", exception.To);
    }
}

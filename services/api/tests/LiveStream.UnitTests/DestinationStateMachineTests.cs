using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Exhaustive, in the same style as <see cref="LiveSessionStateMachineTests"/>: every legal
/// transition is asserted, and every other pair is asserted illegal. The legal set is written out
/// independently of the implementation so the test cannot agree with a mistake by construction.
/// </summary>
public class DestinationStateMachineTests
{
    private static readonly (DestinationStatus From, DestinationStatus To)[] LegalTransitions =
    [
        (DestinationStatus.Idle, DestinationStatus.Preparing),
        (DestinationStatus.Idle, DestinationStatus.Disabled),

        (DestinationStatus.Preparing, DestinationStatus.Connecting),
        (DestinationStatus.Preparing, DestinationStatus.Retrying),
        (DestinationStatus.Preparing, DestinationStatus.Stopping),
        (DestinationStatus.Preparing, DestinationStatus.Error),

        (DestinationStatus.Connecting, DestinationStatus.Live),
        (DestinationStatus.Connecting, DestinationStatus.Retrying),
        (DestinationStatus.Connecting, DestinationStatus.Stopping),
        (DestinationStatus.Connecting, DestinationStatus.Error),

        (DestinationStatus.Live, DestinationStatus.Retrying),
        (DestinationStatus.Live, DestinationStatus.Stopping),
        (DestinationStatus.Live, DestinationStatus.Error),

        (DestinationStatus.Retrying, DestinationStatus.Preparing),
        (DestinationStatus.Retrying, DestinationStatus.Connecting),
        (DestinationStatus.Retrying, DestinationStatus.Stopping),
        (DestinationStatus.Retrying, DestinationStatus.Error),

        (DestinationStatus.Stopping, DestinationStatus.Stopped),
        (DestinationStatus.Stopping, DestinationStatus.Error),

        (DestinationStatus.Stopped, DestinationStatus.Preparing),
        (DestinationStatus.Stopped, DestinationStatus.Disabled),

        (DestinationStatus.Error, DestinationStatus.Preparing),
        (DestinationStatus.Error, DestinationStatus.Stopping),
        (DestinationStatus.Error, DestinationStatus.Stopped),
        (DestinationStatus.Error, DestinationStatus.Disabled),

        (DestinationStatus.Disabled, DestinationStatus.Idle),
    ];

    [Fact]
    public void Every_declared_transition_is_allowed()
    {
        foreach (var (from, to) in LegalTransitions)
        {
            Assert.True(DestinationStateMachine.CanTransition(from, to),
                $"{from} -> {to} should be legal.");
        }
    }

    [Fact]
    public void Every_other_transition_is_rejected()
    {
        var legal = LegalTransitions.ToHashSet();

        foreach (var from in Enum.GetValues<DestinationStatus>())
        {
            foreach (var to in Enum.GetValues<DestinationStatus>())
            {
                if (from == to || legal.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(DestinationStateMachine.CanTransition(from, to),
                    $"{from} -> {to} should be illegal.");
            }
        }
    }

    /// <summary>
    /// The central isolation rule expressed as a transition: a destination can never reach a state
    /// that would imply the session had ended, because it has no authority over the session at all.
    /// </summary>
    [Theory]
    [InlineData(DestinationStatus.Live)]
    [InlineData(DestinationStatus.Connecting)]
    [InlineData(DestinationStatus.Retrying)]
    public void Active_destinations_are_reported_as_active(DestinationStatus status) =>
        Assert.True(DestinationStateMachine.IsActive(status));

    [Theory]
    [InlineData(DestinationStatus.Idle)]
    [InlineData(DestinationStatus.Stopped)]
    [InlineData(DestinationStatus.Error)]
    [InlineData(DestinationStatus.Disabled)]
    public void Settled_destinations_are_not_active(DestinationStatus status)
    {
        Assert.False(DestinationStateMachine.IsActive(status));
        Assert.True(DestinationStateMachine.IsSettled(status));
    }

    /// <summary>
    /// Unlike a failed session, a failed destination is recoverable. An operator who fixes a stream
    /// key must be able to retry it without restarting the whole broadcast.
    /// </summary>
    [Fact]
    public void Error_is_not_terminal()
    {
        Assert.True(DestinationStateMachine.CanTransition(DestinationStatus.Error, DestinationStatus.Preparing));
        Assert.NotEmpty(DestinationStateMachine.AllowedTargets(DestinationStatus.Error));
    }

    [Fact]
    public void Ensure_throws_for_an_illegal_transition()
    {
        var exception = Assert.Throws<InvalidStateTransitionException>(() =>
            DestinationStateMachine.EnsureCanTransition(DestinationStatus.Idle, DestinationStatus.Live));

        Assert.Equal(ErrorCodes.InvalidStateTransition, exception.ErrorCode);
    }

    /// <summary>A destination cannot go live without first being prepared and connected.</summary>
    [Fact]
    public void Idle_cannot_jump_straight_to_live()
    {
        Assert.False(DestinationStateMachine.CanTransition(DestinationStatus.Idle, DestinationStatus.Live));
        Assert.False(DestinationStateMachine.CanTransition(DestinationStatus.Idle, DestinationStatus.Connecting));
    }
}

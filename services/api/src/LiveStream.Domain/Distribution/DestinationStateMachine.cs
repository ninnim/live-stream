using LiveStream.Domain.Common;

namespace LiveStream.Domain.Distribution;

/// <summary>
/// The single authority on which destination state changes are legal.
///
/// Pure and side-effect free, mirroring <see cref="LiveStream.Domain.Sessions.LiveSessionStateMachine"/>
/// so both are exhaustively unit testable and neither can be bypassed by a caller asserting state.
/// </summary>
public static class DestinationStateMachine
{
    private static readonly IReadOnlyDictionary<DestinationStatus, IReadOnlySet<DestinationStatus>> Allowed =
        new Dictionary<DestinationStatus, IReadOnlySet<DestinationStatus>>
        {
            [DestinationStatus.Idle] = Set(
                DestinationStatus.Preparing,
                DestinationStatus.Disabled),

            [DestinationStatus.Preparing] = Set(
                DestinationStatus.Connecting,
                DestinationStatus.Retrying,
                DestinationStatus.Stopping,
                DestinationStatus.Error),

            [DestinationStatus.Connecting] = Set(
                DestinationStatus.Live,
                DestinationStatus.Retrying,
                DestinationStatus.Stopping,
                DestinationStatus.Error),

            [DestinationStatus.Live] = Set(
                DestinationStatus.Retrying,
                DestinationStatus.Stopping,
                DestinationStatus.Error),

            [DestinationStatus.Retrying] = Set(
                DestinationStatus.Preparing,
                DestinationStatus.Connecting,
                DestinationStatus.Stopping,
                DestinationStatus.Error),

            [DestinationStatus.Stopping] = Set(
                DestinationStatus.Stopped,
                DestinationStatus.Error),

            // Not terminal, unlike a failed session: an operator can retry a destination, and the
            // next session starts it again from scratch.
            [DestinationStatus.Stopped] = Set(
                DestinationStatus.Preparing,
                DestinationStatus.Disabled),

            [DestinationStatus.Error] = Set(
                DestinationStatus.Preparing,
                DestinationStatus.Stopping,
                DestinationStatus.Stopped,
                DestinationStatus.Disabled),

            [DestinationStatus.Disabled] = Set(
                DestinationStatus.Idle),
        };

    /// <summary>States in which the relay should be running or expected back shortly.</summary>
    public static readonly IReadOnlySet<DestinationStatus> ActiveStates = Set(
        DestinationStatus.Preparing,
        DestinationStatus.Connecting,
        DestinationStatus.Live,
        DestinationStatus.Retrying);

    public static bool CanTransition(DestinationStatus from, DestinationStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(DestinationStatus from, DestinationStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(from.ToString(), to.ToString());
        }
    }

    public static IReadOnlySet<DestinationStatus> AllowedTargets(DestinationStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : Set();

    /// <summary>True when a relay process should exist for this destination.</summary>
    public static bool IsActive(DestinationStatus status) => ActiveStates.Contains(status);

    /// <summary>True when the destination has settled and needs no further work this session.</summary>
    public static bool IsSettled(DestinationStatus status) =>
        status is DestinationStatus.Stopped or DestinationStatus.Error or DestinationStatus.Disabled
            or DestinationStatus.Idle;

    private static IReadOnlySet<DestinationStatus> Set(params DestinationStatus[] values) =>
        new HashSet<DestinationStatus>(values);
}

using LiveStream.Domain.Common;

namespace LiveStream.Domain.Sessions;

/// <summary>
/// The single authority on which Live Session state changes are legal.
/// Pure and side-effect free so it can be exhaustively unit tested.
/// The server owns this; clients never assert state (ai/architecture-rules.md).
/// </summary>
public static class LiveSessionStateMachine
{
    private static readonly IReadOnlyDictionary<LiveSessionStatus, IReadOnlySet<LiveSessionStatus>> Allowed =
        new Dictionary<LiveSessionStatus, IReadOnlySet<LiveSessionStatus>>
        {
            [LiveSessionStatus.Draft] = Set(
                LiveSessionStatus.Preparing,
                LiveSessionStatus.Ended),

            [LiveSessionStatus.Preparing] = Set(
                LiveSessionStatus.Ready,
                LiveSessionStatus.Failed,
                LiveSessionStatus.Ended),

            [LiveSessionStatus.Ready] = Set(
                LiveSessionStatus.Starting,
                LiveSessionStatus.Preparing,
                LiveSessionStatus.Failed,
                LiveSessionStatus.Ended),

            [LiveSessionStatus.Starting] = Set(
                LiveSessionStatus.Live,
                LiveSessionStatus.Failed,
                LiveSessionStatus.Stopping),

            [LiveSessionStatus.Live] = Set(
                LiveSessionStatus.Degraded,
                LiveSessionStatus.Reconnecting,
                LiveSessionStatus.Stopping,
                LiveSessionStatus.Failed),

            [LiveSessionStatus.Degraded] = Set(
                LiveSessionStatus.Live,
                LiveSessionStatus.Reconnecting,
                LiveSessionStatus.Stopping,
                LiveSessionStatus.Failed),

            [LiveSessionStatus.Reconnecting] = Set(
                LiveSessionStatus.Live,
                LiveSessionStatus.Degraded,
                LiveSessionStatus.Stopping,
                LiveSessionStatus.Failed),

            [LiveSessionStatus.Stopping] = Set(
                LiveSessionStatus.Ended,
                LiveSessionStatus.Failed),

            [LiveSessionStatus.Ended] = Set(),

            [LiveSessionStatus.Failed] = Set(),
        };

    /// <summary>States in which media is actively flowing (or expected to resume shortly).</summary>
    public static readonly IReadOnlySet<LiveSessionStatus> BroadcastingStates = Set(
        LiveSessionStatus.Live,
        LiveSessionStatus.Degraded,
        LiveSessionStatus.Reconnecting);

    public static bool CanTransition(LiveSessionStatus from, LiveSessionStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Throws <see cref="InvalidStateTransitionException"/> when the transition is illegal.</summary>
    public static void EnsureCanTransition(LiveSessionStatus from, LiveSessionStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(from.ToString(), to.ToString());
        }
    }

    public static IReadOnlySet<LiveSessionStatus> AllowedTargets(LiveSessionStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : Set();

    public static bool IsTerminal(LiveSessionStatus status) =>
        status is LiveSessionStatus.Ended or LiveSessionStatus.Failed;

    public static bool IsBroadcasting(LiveSessionStatus status) => BroadcastingStates.Contains(status);

    /// <summary>True when the media plane should hold an ingest path open for this state.</summary>
    public static bool ExpectsIngest(LiveSessionStatus status) =>
        status is LiveSessionStatus.Ready
            or LiveSessionStatus.Starting
            or LiveSessionStatus.Live
            or LiveSessionStatus.Degraded
            or LiveSessionStatus.Reconnecting;

    private static IReadOnlySet<LiveSessionStatus> Set(params LiveSessionStatus[] values) =>
        new HashSet<LiveSessionStatus>(values);
}

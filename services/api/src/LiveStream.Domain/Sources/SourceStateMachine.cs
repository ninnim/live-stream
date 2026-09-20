using LiveStream.Domain.Common;

namespace LiveStream.Domain.Sources;

/// <summary>
/// The single authority on which source state changes are legal.
///
/// Pure and side-effect free, matching the session and destination machines, so all three are
/// exhaustively unit testable and none can be bypassed by a caller asserting state.
/// </summary>
public static class SourceStateMachine
{
    private static readonly IReadOnlyDictionary<SourceStatus, IReadOnlySet<SourceStatus>> Allowed =
        new Dictionary<SourceStatus, IReadOnlySet<SourceStatus>>
        {
            [SourceStatus.Invited] = Set(
                SourceStatus.Paired,
                SourceStatus.Revoked),

            [SourceStatus.Paired] = Set(
                SourceStatus.Connected,
                SourceStatus.Disconnected,
                SourceStatus.Revoked),

            // A source that stops sending media is Disconnected, never Revoked: a phone going
            // through a tunnel must be able to come back without pairing again.
            [SourceStatus.Connected] = Set(
                SourceStatus.Disconnected,
                SourceStatus.Revoked),

            [SourceStatus.Disconnected] = Set(
                SourceStatus.Connected,
                SourceStatus.Revoked),

            // Terminal. docs/05-multi-device.md: "Revocation is immediate." Allowing a way back
            // would mean a withdrawn device could return without a new code.
            [SourceStatus.Revoked] = Set(),
        };

    /// <summary>States in which the source is paired and may hold credentials.</summary>
    public static readonly IReadOnlySet<SourceStatus> ActiveStates = Set(
        SourceStatus.Paired,
        SourceStatus.Connected,
        SourceStatus.Disconnected);

    public static bool CanTransition(SourceStatus from, SourceStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(SourceStatus from, SourceStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(from.ToString(), to.ToString());
        }
    }

    public static IReadOnlySet<SourceStatus> AllowedTargets(SourceStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : Set();

    public static bool IsTerminal(SourceStatus status) => status is SourceStatus.Revoked;

    /// <summary>True when the source may still authenticate and publish.</summary>
    public static bool IsActive(SourceStatus status) => ActiveStates.Contains(status);

    /// <summary>True when the media plane should hold an ingest path open for this source.</summary>
    public static bool ExpectsIngest(SourceStatus status) =>
        status is SourceStatus.Paired or SourceStatus.Connected or SourceStatus.Disconnected;

    private static IReadOnlySet<SourceStatus> Set(params SourceStatus[] values) =>
        new HashSet<SourceStatus>(values);
}

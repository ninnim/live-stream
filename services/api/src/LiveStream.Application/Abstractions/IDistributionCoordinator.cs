namespace LiveStream.Application.Abstractions;

/// <summary>
/// The one seam through which the session lifecycle touches multi-platform distribution.
///
/// Kept deliberately narrow so <c>LiveSessionService</c> and <c>LiveSessionReconciler</c> never
/// learn what a destination is. It also enforces the isolation requirement structurally: both
/// methods return <see cref="Task"/> rather than a result, so there is nothing for session logic to
/// branch on even if someone later wanted to
/// (implementation/phase-2: "Core stream can remain LIVE if one destination fails").
///
/// Implementations must not throw. A session must reach LIVE and ENDED regardless of what the
/// external platforms are doing.
/// </summary>
public interface IDistributionCoordinator
{
    /// <summary>Called once when a session reaches LIVE. Starts every enabled destination.</summary>
    Task OnSessionLiveAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Called when a session leaves the broadcasting states, for any reason.</summary>
    Task OnSessionEndedAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Used when distribution is not part of the running configuration — and by tests that are
/// exercising session lifecycle rules rather than destination behaviour.
/// </summary>
public sealed class NullDistributionCoordinator : IDistributionCoordinator
{
    public Task OnSessionLiveAsync(Guid sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnSessionEndedAsync(Guid sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
}

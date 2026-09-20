using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Common;

/// <summary>
/// Which parts of the platform this process runs. One binary, composed differently per deployment,
/// so the request tier and the background tier scale on their own metrics
/// (implementation/phase-7: "The platform can scale components independently").
/// </summary>
public enum RuntimeRole
{
    /// <summary>Serves HTTP and runs the background loops. The default, and the only sane single-instance setup.</summary>
    All = 0,

    /// <summary>Serves HTTP only. Add replicas for request volume without multiplying background work.</summary>
    Api = 1,

    /// <summary>Runs the background loops only. Scales with sessions in flight, not with requests.</summary>
    Worker = 2,
}

/// <summary>
/// How this process behaves as one instance among several. Bound from the <c>Runtime</c> section.
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    /// <summary><see cref="RuntimeRole"/>. Defaults to <c>All</c> so a single-container run works unconfigured.</summary>
    public RuntimeRole Role { get; set; } = RuntimeRole.All;

    /// <summary>
    /// Identifies this instance in leases and logs. Defaults to the machine name plus a random
    /// suffix, which is distinct per process even when two run on one host.
    /// </summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// The region this deployment serves, e.g. <c>eu-central</c>. Matched against a workspace's
    /// residency requirement; leave empty in a single-region deployment, where nothing is pinned.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Whether background loops must hold a lease before doing work. Off is only safe when exactly
    /// one instance runs them — two unleased instances reconcile the same session and pay for the
    /// same AI job twice.
    /// </summary>
    public bool LeaderElection { get; set; } = true;

    /// <summary>
    /// How long a lease stays claimed without renewal. This is the upper bound on how long
    /// background work pauses after an instance is killed, so it is also an input to the SLOs in
    /// docs/12-observability-and-reliability.md.
    /// </summary>
    [Range(5, 300)]
    public int LeaseTtlSeconds { get; set; } = 30;

    /// <summary>
    /// How long readiness reports "draining" before the host starts shutting components down. It
    /// must exceed the load balancer's health-check interval, or requests will be routed to a
    /// process that has already stopped accepting them.
    /// </summary>
    [Range(0, 120)]
    public int ShutdownDrainSeconds { get; set; } = 5;

    public bool RunsBackgroundWork => Role is RuntimeRole.All or RuntimeRole.Worker;

    public bool ServesRequests => Role is RuntimeRole.All or RuntimeRole.Api;

    public TimeSpan LeaseTtl => TimeSpan.FromSeconds(LeaseTtlSeconds);

    public TimeSpan ShutdownDrain => TimeSpan.FromSeconds(ShutdownDrainSeconds);
}

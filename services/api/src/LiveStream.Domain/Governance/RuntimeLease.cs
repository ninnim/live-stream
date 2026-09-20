namespace LiveStream.Domain.Governance;

/// <summary>
/// A named, time-limited claim on a background job, held by one process at a time.
///
/// Every background loop in this platform is a singleton by nature: two instances reconciling the
/// same session race each other, and two instances running the same AI job pay for it twice. Once
/// the API runs as more than one replica — which is the whole point of Phase 7 — something has to
/// decide which replica does that work. This row is that decision, and the database is the only
/// component all replicas already agree on.
///
/// The lease expires rather than being released, so a replica that is killed without warning holds
/// the work for at most <see cref="ExpiresAt"/> minus now, and never forever.
/// </summary>
public class RuntimeLease
{
    /// <summary>Primary key: the name of the work, e.g. <c>stream-health</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Instance that currently holds the lease. Diagnostic, and the renewal predicate.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>When the current owner took over. Reset on takeover, not on renewal.</summary>
    public DateTimeOffset AcquiredAt { get; set; }

    /// <summary>
    /// When the claim lapses. A renewal pushes it forward; a takeover is only permitted once it is
    /// in the past.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Incremented on every takeover, never on renewal. It is what makes a takeover detectable:
    /// a holder that sees the token move knows another instance now owns the work, whatever its
    /// own clock says.
    /// </summary>
    public long FencingToken { get; set; }

    /// <summary>
    /// Optimistic concurrency token, bumped on every write including a renewal.
    ///
    /// It has to move on renewal too. If renewals left it alone, an instance that read an
    /// expired lease could commit a takeover just after the owner renewed it, and both would
    /// believe they held the work.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Names of the leases this platform takes. One per background loop, so the work can spread.</summary>
    public static class Names
    {
        public const string StreamHealth = "stream-health";
        public const string Destinations = "destinations";
        public const string SourcePresence = "source-presence";
        public const string AiJobs = "ai-jobs";
        public const string Retention = "retention";
    }
}

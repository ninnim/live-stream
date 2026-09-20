using System.ComponentModel.DataAnnotations;
using LiveStream.Domain.Governance;

namespace LiveStream.Application.Common;

/// <summary>
/// Tenant governance: what a new workspace is entitled to, and how retention is enforced.
/// Bound from the <c>Governance</c> section.
/// </summary>
public sealed class GovernanceOptions
{
    public const string SectionName = "Governance";

    /// <summary>
    /// Plan every new workspace starts on. Defaults to <c>Pro</c>, whose allowances are the limits
    /// this platform has shipped since Phase 1 — so turning plans on changes nothing until an
    /// operator moves someone. A deployment with billing sets this to <c>Free</c>.
    /// </summary>
    public WorkspacePlan DefaultPlan { get; set; } = WorkspacePlan.Pro;

    /// <summary>
    /// Whether expired recordings are actually deleted. Off keeps the reported expiry date and
    /// deletes nothing, which is the honest setting while an operator is validating a policy.
    /// </summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention sweep runs. Hourly is ample for a policy measured in days.</summary>
    [Range(60, 86400)]
    public int RetentionSweepIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Recordings deleted per sweep. Bounded so a policy tightened from 365 days to 7 deletes
    /// steadily over some hours instead of hammering storage in one pass.
    /// </summary>
    [Range(1, 1000)]
    public int RetentionBatchSize { get; set; } = 100;

    /// <summary>Upper bound on sessions in one workspace export, so the response cannot be unbounded.</summary>
    [Range(1, 10000)]
    public int ExportMaxSessions { get; set; } = 1000;

    public TimeSpan RetentionSweepInterval => TimeSpan.FromSeconds(RetentionSweepIntervalSeconds);
}

/// <summary>
/// Unit rates used to estimate what usage costs. Bound from the <c>Costs</c> section.
///
/// Every rate is nullable and every one of them starts unset, because a cost report that shows
/// $0.00 reads as free rather than as unconfigured (the same rule the AI job pricing follows).
/// </summary>
public sealed class CostOptions
{
    public const string SectionName = "Costs";

    /// <summary>ISO currency code the rates are quoted in. Display only; no conversion happens.</summary>
    [RegularExpression("^[A-Z]{3}$")]
    public string Currency { get; set; } = "USD";

    /// <summary>Delivery cost per gigabyte sent to viewers.</summary>
    [Range(0, 1000)]
    public decimal? EgressPerGb { get; set; }

    /// <summary>Storage cost per gigabyte-month of retained recordings.</summary>
    [Range(0, 1000)]
    public decimal? RecordingStoragePerGbMonth { get; set; }

    /// <summary>Ingest and packaging cost per hour a session spends broadcasting.</summary>
    [Range(0, 1000)]
    public decimal? StreamingPerHour { get; set; }

    /// <summary>Cost per hour of one outbound relay to an external platform.</summary>
    [Range(0, 1000)]
    public decimal? DestinationRelayPerHour { get; set; }

    public bool IsConfigured =>
        EgressPerGb is not null || RecordingStoragePerGbMonth is not null
        || StreamingPerHour is not null || DestinationRelayPerHour is not null;
}

namespace LiveStream.Domain.Governance;

/// <summary>
/// Commercial tier of a workspace. The plan sets the ceiling for every tenant limit; a workspace
/// admin may tighten a limit below it but never raise one above it, so changing what a customer is
/// entitled to is always a deliberate operator action rather than a self-service API call
/// (implementation/phase-7: "Advanced tenant limits").
/// </summary>
public enum WorkspacePlan
{
    Free = 0,
    Pro = 1,
    Business = 2,
    Enterprise = 3,
}

/// <summary>
/// What a plan entitles a workspace to. Pure data so the entitlement table is unit testable and
/// cannot drift between the services that enforce it.
/// </summary>
public sealed record PlanAllowance(
    int MaxConcurrentSessions,
    int MaxDestinationsPerSession,
    int MaxSourcesPerSession,
    int RecordingRetentionDays,
    bool SingleSignOn,
    bool DataResidency);

public static class WorkspacePlans
{
    /// <summary>
    /// Longest retention any plan grants. Also the hard cap on a per-workspace override, so a
    /// misconfigured value cannot turn retention off by setting it to a century.
    /// </summary>
    public const int MaxRetentionDays = 3650;

    private static readonly IReadOnlyDictionary<WorkspacePlan, PlanAllowance> Allowances =
        new Dictionary<WorkspacePlan, PlanAllowance>
        {
            [WorkspacePlan.Free] = new(
                MaxConcurrentSessions: 1,
                MaxDestinationsPerSession: 1,
                MaxSourcesPerSession: 2,
                RecordingRetentionDays: 7,
                SingleSignOn: false,
                DataResidency: false),
            [WorkspacePlan.Pro] = new(
                MaxConcurrentSessions: 3,
                MaxDestinationsPerSession: 5,
                MaxSourcesPerSession: 8,
                RecordingRetentionDays: 30,
                SingleSignOn: false,
                DataResidency: false),
            [WorkspacePlan.Business] = new(
                MaxConcurrentSessions: 10,
                MaxDestinationsPerSession: 10,
                MaxSourcesPerSession: 12,
                RecordingRetentionDays: 90,
                SingleSignOn: true,
                DataResidency: true),
            [WorkspacePlan.Enterprise] = new(
                MaxConcurrentSessions: 50,
                MaxDestinationsPerSession: 20,
                MaxSourcesPerSession: 16,
                RecordingRetentionDays: 365,
                SingleSignOn: true,
                DataResidency: true),
        };

    /// <summary>
    /// The plan assumed when none is recorded. Free is the most restrictive tier on purpose: a
    /// workspace whose limits row is somehow missing must not be treated as unlimited. What new
    /// workspaces actually get is a deployment setting (<c>Governance:DefaultPlan</c>), because a
    /// deployment with no billing system wants every workspace on one tier.
    /// </summary>
    public const WorkspacePlan Fallback = WorkspacePlan.Free;

    public static PlanAllowance For(WorkspacePlan plan) =>
        Allowances.TryGetValue(plan, out var allowance) ? allowance : Allowances[Fallback];
}

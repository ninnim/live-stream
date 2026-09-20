using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;

namespace LiveStream.Domain.Governance;

/// <summary>
/// Per-workspace limits and data-governance settings, layered on top of the workspace's plan.
///
/// Overrides may only tighten: the effective value is the smaller of the plan's allowance and the
/// override. That is enforced twice on purpose — once when a value is set, so an admin gets a clear
/// error, and once when it is read, so a row written before a plan downgrade cannot leave a
/// workspace above its entitlement.
/// </summary>
public class WorkspaceLimits
{
    /// <summary>Primary key: one row per workspace, so limits cannot be ambiguous.</summary>
    public Guid WorkspaceId { get; set; }

    public WorkspacePlan Plan { get; set; } = WorkspacePlans.Fallback;

    public int? MaxConcurrentSessionsOverride { get; set; }

    public int? MaxDestinationsPerSessionOverride { get; set; }

    public int? MaxSourcesPerSessionOverride { get; set; }

    public int? RecordingRetentionDaysOverride { get; set; }

    /// <summary>
    /// Region this workspace's media and recordings must stay in, or <c>null</c> for no
    /// requirement. Matched against the region a deployment declares it serves.
    /// </summary>
    public string? ResidencyRegion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Workspace? Workspace { get; set; }

    public PlanAllowance Allowance => WorkspacePlans.For(Plan);

    public int EffectiveMaxConcurrentSessions =>
        Tighten(Allowance.MaxConcurrentSessions, MaxConcurrentSessionsOverride);

    public int EffectiveMaxDestinationsPerSession =>
        Tighten(Allowance.MaxDestinationsPerSession, MaxDestinationsPerSessionOverride);

    public int EffectiveMaxSourcesPerSession =>
        Tighten(Allowance.MaxSourcesPerSession, MaxSourcesPerSessionOverride);

    public int EffectiveRecordingRetentionDays =>
        Tighten(Allowance.RecordingRetentionDays, RecordingRetentionDaysOverride);

    /// <summary>Limits for a workspace that has no row yet: the default plan, no overrides.</summary>
    public static WorkspaceLimits DefaultFor(Guid workspaceId, WorkspacePlan plan, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId,
        Plan = plan,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Replaces every workspace-admin-settable value at once — PUT semantics, so <c>null</c> means
    /// "no override, use the plan's allowance" rather than "leave whatever was there".
    /// A partial update would make clearing an override impossible to express.
    /// </summary>
    public void ReplaceOverrides(
        int? maxConcurrentSessions,
        int? maxDestinationsPerSession,
        int? maxSourcesPerSession,
        int? recordingRetentionDays,
        string? residencyRegion,
        DateTimeOffset now)
    {
        var region = NormalizeRegion(residencyRegion);

        if (region is not null && !Allowance.DataResidency)
        {
            throw new DomainException(ErrorCodes.PlanLimitReached,
                $"Data residency is not part of the {Plan} plan.");
        }

        MaxConcurrentSessionsOverride =
            ValidateOverride(maxConcurrentSessions, Allowance.MaxConcurrentSessions, "concurrent sessions");
        MaxDestinationsPerSessionOverride = ValidateOverride(maxDestinationsPerSession,
            Allowance.MaxDestinationsPerSession, "destinations per session");
        MaxSourcesPerSessionOverride = ValidateOverride(maxSourcesPerSession,
            Allowance.MaxSourcesPerSession, "sources per session");
        RecordingRetentionDaysOverride = ValidateOverride(recordingRetentionDays,
            Allowance.RecordingRetentionDays, "days of recording retention");
        ResidencyRegion = region;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the workspace to another plan. Overrides that no longer fit are dropped rather than
    /// kept: after a downgrade the plan's own allowance is the tighter of the two, so a stale
    /// override would be dead configuration that reappears on a later upgrade.
    /// </summary>
    public void ChangePlan(WorkspacePlan plan, DateTimeOffset now)
    {
        Plan = plan;

        var allowance = WorkspacePlans.For(plan);
        MaxConcurrentSessionsOverride = DropIfLoosened(MaxConcurrentSessionsOverride, allowance.MaxConcurrentSessions);
        MaxDestinationsPerSessionOverride =
            DropIfLoosened(MaxDestinationsPerSessionOverride, allowance.MaxDestinationsPerSession);
        MaxSourcesPerSessionOverride = DropIfLoosened(MaxSourcesPerSessionOverride, allowance.MaxSourcesPerSession);
        RecordingRetentionDaysOverride =
            DropIfLoosened(RecordingRetentionDaysOverride, allowance.RecordingRetentionDays);

        if (!allowance.DataResidency)
        {
            ResidencyRegion = null;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Normalizes a region identifier. Kept deliberately narrow — lower-case letters, digits and
    /// hyphens — because the value is compared against a deployment's declared region and appears
    /// in operator tooling.
    /// </summary>
    public static string? NormalizeRegion(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > 32 || !trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "A region is up to 32 characters of lower-case letters, digits, and hyphens.");
        }

        return trimmed;
    }

    private int? ValidateOverride(int? value, int allowance, string what)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Value < 1)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"A limit on {what} must be at least 1.");
        }

        if (value.Value > allowance)
        {
            throw new DomainException(ErrorCodes.PlanLimitReached,
                $"The {Plan} plan allows {allowance} {what}. A workspace setting can lower that, not raise it.");
        }

        return value.Value;
    }

    private static int? DropIfLoosened(int? over, int allowance) => over is null || over.Value >= allowance ? null : over;

    private static int Tighten(int allowance, int? over) => over is null ? allowance : Math.Min(allowance, over.Value);
}

using LiveStream.Domain.Common;
using LiveStream.Domain.Governance;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Tenant limits (implementation/phase-7: "Advanced tenant limits").
///
/// The rule these tests exist to protect: a workspace admin may tighten a limit, never loosen one.
/// If that ever stops being true, plans stop meaning anything, because the tenant sets its own.
/// </summary>
public class WorkspaceLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_workspace_with_no_overrides_gets_exactly_its_plan()
    {
        var limits = Build(WorkspacePlan.Business);
        var allowance = WorkspacePlans.For(WorkspacePlan.Business);

        Assert.Equal(allowance.MaxConcurrentSessions, limits.EffectiveMaxConcurrentSessions);
        Assert.Equal(allowance.MaxDestinationsPerSession, limits.EffectiveMaxDestinationsPerSession);
        Assert.Equal(allowance.MaxSourcesPerSession, limits.EffectiveMaxSourcesPerSession);
        Assert.Equal(allowance.RecordingRetentionDays, limits.EffectiveRecordingRetentionDays);
    }

    [Fact]
    public void An_override_may_tighten_a_limit()
    {
        var limits = Build(WorkspacePlan.Business);

        limits.ReplaceOverrides(2, 1, 3, 14, null, Now);

        Assert.Equal(2, limits.EffectiveMaxConcurrentSessions);
        Assert.Equal(1, limits.EffectiveMaxDestinationsPerSession);
        Assert.Equal(3, limits.EffectiveMaxSourcesPerSession);
        Assert.Equal(14, limits.EffectiveRecordingRetentionDays);
    }

    [Fact]
    public void An_override_may_not_loosen_a_limit()
    {
        var limits = Build(WorkspacePlan.Free);
        var allowed = WorkspacePlans.For(WorkspacePlan.Free).MaxConcurrentSessions;

        var error = Assert.Throws<DomainException>(() =>
            limits.ReplaceOverrides(allowed + 1, null, null, null, null, Now));

        Assert.Equal(ErrorCodes.PlanLimitReached, error.ErrorCode);
    }

    [Fact]
    public void A_stored_override_above_the_plan_is_still_clamped_when_read()
    {
        // Belt and braces. Validation refuses to write one, but a plan downgrade could leave a row
        // that was legal when it was saved, and reading it must not hand back the old entitlement.
        var limits = Build(WorkspacePlan.Free);
        limits.MaxConcurrentSessionsOverride = 99;

        Assert.Equal(WorkspacePlans.For(WorkspacePlan.Free).MaxConcurrentSessions,
            limits.EffectiveMaxConcurrentSessions);
    }

    [Fact]
    public void Passing_null_clears_an_override_rather_than_leaving_it()
    {
        // PUT semantics. A partial update would give no way to say "go back to the plan's value".
        var limits = Build(WorkspacePlan.Business);
        limits.ReplaceOverrides(2, null, null, null, null, Now);

        limits.ReplaceOverrides(null, null, null, null, null, Now);

        Assert.Null(limits.MaxConcurrentSessionsOverride);
        Assert.Equal(WorkspacePlans.For(WorkspacePlan.Business).MaxConcurrentSessions,
            limits.EffectiveMaxConcurrentSessions);
    }

    [Fact]
    public void Data_residency_requires_a_plan_that_includes_it()
    {
        var limits = Build(WorkspacePlan.Pro);

        var error = Assert.Throws<DomainException>(() =>
            limits.ReplaceOverrides(null, null, null, null, "eu-central", Now));

        Assert.Equal(ErrorCodes.PlanLimitReached, error.ErrorCode);
    }

    [Fact]
    public void A_downgrade_drops_overrides_the_new_plan_makes_redundant_and_clears_residency()
    {
        var limits = Build(WorkspacePlan.Business);
        limits.ReplaceOverrides(8, 8, 10, 60, "eu-central", Now);

        limits.ChangePlan(WorkspacePlan.Free, Now.AddDays(1));

        // Every override was looser than Free allows, so Free's own allowance is what applies and
        // keeping the numbers would only make them reappear on a later upgrade.
        Assert.Null(limits.MaxConcurrentSessionsOverride);
        Assert.Null(limits.RecordingRetentionDaysOverride);
        Assert.Null(limits.ResidencyRegion);
        Assert.Equal(WorkspacePlans.For(WorkspacePlan.Free).MaxConcurrentSessions,
            limits.EffectiveMaxConcurrentSessions);
    }

    [Fact]
    public void A_downgrade_keeps_an_override_that_is_still_the_tighter_choice()
    {
        var limits = Build(WorkspacePlan.Enterprise);
        limits.ReplaceOverrides(1, null, null, 3, null, Now);

        limits.ChangePlan(WorkspacePlan.Pro, Now.AddDays(1));

        Assert.Equal(1, limits.MaxConcurrentSessionsOverride);
        Assert.Equal(3, limits.EffectiveRecordingRetentionDays);
    }

    [Theory]
    [InlineData("EU-Central", "eu-central")]
    [InlineData("  us-east-1  ", "us-east-1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Region_identifiers_are_normalized(string? input, string? expected) =>
        Assert.Equal(expected, WorkspaceLimits.NormalizeRegion(input));

    [Theory]
    [InlineData("eu central")]
    [InlineData("eu_central")]
    [InlineData("../etc")]
    public void A_region_that_is_not_an_identifier_is_refused(string input)
    {
        var error = Assert.Throws<DomainException>(() => WorkspaceLimits.NormalizeRegion(input));
        Assert.Equal(ErrorCodes.ValidationFailed, error.ErrorCode);
    }

    [Fact]
    public void Every_plan_has_an_allowance_and_they_are_ordered()
    {
        // A plan added without a row would silently fall back to Free, which is the sort of thing
        // that is only noticed by the customer who paid for the higher tier.
        var plans = Enum.GetValues<WorkspacePlan>().ToList();

        foreach (var plan in plans)
        {
            Assert.NotNull(WorkspacePlans.For(plan));
        }

        Assert.True(WorkspacePlans.For(WorkspacePlan.Free).MaxConcurrentSessions
                    < WorkspacePlans.For(WorkspacePlan.Pro).MaxConcurrentSessions);
        Assert.True(WorkspacePlans.For(WorkspacePlan.Pro).MaxConcurrentSessions
                    < WorkspacePlans.For(WorkspacePlan.Business).MaxConcurrentSessions);
        Assert.True(WorkspacePlans.For(WorkspacePlan.Business).MaxConcurrentSessions
                    < WorkspacePlans.For(WorkspacePlan.Enterprise).MaxConcurrentSessions);
    }

    [Fact]
    public void Single_sign_on_and_residency_are_business_tier_features()
    {
        Assert.False(WorkspacePlans.For(WorkspacePlan.Free).SingleSignOn);
        Assert.False(WorkspacePlans.For(WorkspacePlan.Pro).SingleSignOn);
        Assert.True(WorkspacePlans.For(WorkspacePlan.Business).SingleSignOn);
        Assert.True(WorkspacePlans.For(WorkspacePlan.Enterprise).DataResidency);
    }

    private static WorkspaceLimits Build(WorkspacePlan plan) =>
        WorkspaceLimits.DefaultFor(Guid.NewGuid(), plan, Now);
}

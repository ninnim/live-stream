using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Domain.Governance;
using Microsoft.Extensions.Options;

namespace LiveStream.TestSupport;

/// <summary>
/// Builds the Phase 7 governance services for unit tests.
///
/// The default plan is Enterprise, so plan entitlements stay out of the way of tests that are about
/// something else — the deployment options are then the tighter of the two, exactly as they were
/// before plans existed. A test that is about plan limits passes the plan it means.
/// </summary>
public static class TestGovernance
{
    public static TenantLimitService Limits(
        IAppDbContext db,
        IClock clock,
        WorkspacePlan defaultPlan = WorkspacePlan.Enterprise,
        string? region = null,
        LiveSessionOptions? sessions = null,
        SourceOptions? sources = null,
        DistributionOptions? distribution = null) =>
        new(db,
            clock,
            Options.Create(new GovernanceOptions { DefaultPlan = defaultPlan }),
            Options.Create(new RuntimeOptions { Region = region }),
            Options.Create(sessions ?? new LiveSessionOptions()),
            Options.Create(sources ?? new SourceOptions()),
            Options.Create(distribution ?? new DistributionOptions()));
}

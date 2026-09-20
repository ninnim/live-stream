using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Domain.Common;
using LiveStream.Domain.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Governance;

/// <summary>
/// What one workspace is actually allowed, right now.
///
/// Three things decide it, and the answer is the tightest of them: the deployment's own caps
/// (a resource guard — no plan can make one machine serve more than it can), the workspace's plan
/// (a commercial entitlement), and the workspace's own overrides (a customer choosing to be
/// stricter than they have to be).
/// </summary>
public sealed record EffectiveLimits(
    WorkspacePlan Plan,
    int MaxConcurrentSessions,
    int MaxDestinationsPerSession,
    int MaxSourcesPerSession,
    int RecordingRetentionDays,
    string? ResidencyRegion,
    bool SingleSignOnAllowed,
    bool DataResidencyAllowed);

public sealed class TenantLimitService(
    IAppDbContext db,
    IClock clock,
    IOptions<GovernanceOptions> governance,
    IOptions<RuntimeOptions> runtime,
    IOptions<LiveSessionOptions> sessions,
    IOptions<SourceOptions> sources,
    IOptions<DistributionOptions> distribution)
{
    private readonly GovernanceOptions _governance = governance.Value;
    private readonly RuntimeOptions _runtime = runtime.Value;

    /// <summary>The plan a workspace created right now would start on.</summary>
    public WorkspacePlan DefaultPlan => _governance.DefaultPlan;

    /// <summary>
    /// The stored limits row, or an unsaved default for a workspace that has none. Nothing is
    /// written here: reads must not create rows, or a probe for a workspace that does not exist
    /// would leave a trail of them.
    /// </summary>
    public async Task<WorkspaceLimits> GetAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var limits = await db.WorkspaceLimits
            .Include(l => l.Workspace)
            .FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId, cancellationToken);

        return limits ?? WorkspaceLimits.DefaultFor(workspaceId, _governance.DefaultPlan, clock.UtcNow);
    }

    /// <summary>The stored row, creating it on first use. For write paths only.</summary>
    public async Task<WorkspaceLimits> GetOrCreateAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var limits = await db.WorkspaceLimits.FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId, cancellationToken);

        if (limits is not null)
        {
            return limits;
        }

        limits = WorkspaceLimits.DefaultFor(workspaceId, _governance.DefaultPlan, clock.UtcNow);
        db.WorkspaceLimits.Add(limits);
        return limits;
    }

    public async Task<EffectiveLimits> ResolveAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Resolve(await GetAsync(workspaceId, cancellationToken));

    /// <summary>Effective limits for the workspace that owns a session.</summary>
    public async Task<EffectiveLimits> ResolveForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var workspaceId = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => (Guid?)s.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);

        return workspaceId is null
            ? Resolve(WorkspaceLimits.DefaultFor(Guid.Empty, _governance.DefaultPlan, clock.UtcNow))
            : await ResolveAsync(workspaceId.Value, cancellationToken);
    }

    public EffectiveLimits Resolve(WorkspaceLimits limits) => new(
        limits.Plan,
        Math.Min(sessions.Value.MaxConcurrentSessionsPerWorkspace, limits.EffectiveMaxConcurrentSessions),
        Math.Min(distribution.Value.MaxDestinationsPerSession, limits.EffectiveMaxDestinationsPerSession),
        Math.Min(sources.Value.MaxSourcesPerSession, limits.EffectiveMaxSourcesPerSession),
        limits.EffectiveRecordingRetentionDays,
        limits.ResidencyRegion,
        limits.Allowance.SingleSignOn,
        limits.Allowance.DataResidency);

    /// <summary>
    /// Refuses to serve a workspace whose data must stay in another region.
    ///
    /// This runs where media is provisioned rather than at the edge, because it is the media and
    /// the recording that are pinned, and because a request that reached the wrong region must fail
    /// loudly. Quietly serving it would put the tenant's media in a region they excluded, and
    /// nothing downstream would ever notice.
    /// </summary>
    public void EnsureRegionAllowed(EffectiveLimits limits)
    {
        if (limits.ResidencyRegion is null)
        {
            return;
        }

        var deploymentRegion = WorkspaceLimits.NormalizeRegion(_runtime.Region);

        if (!string.Equals(limits.ResidencyRegion, deploymentRegion, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.RegionNotAvailable,
                $"This workspace keeps its data in {limits.ResidencyRegion}. Use that region's address.");
        }
    }
}

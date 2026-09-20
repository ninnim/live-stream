using LiveStream.Application.Abstractions;
using LiveStream.Application.Ai;
using LiveStream.Application.Common;
using LiveStream.Application.Governance.Contracts;
using LiveStream.Domain.Ai;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using LiveStream.Domain.Governance;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Governance;

/// <summary>
/// Capacity and cost visibility — the phase's third acceptance criterion
/// (implementation/phase-7: "provide operators with clear capacity and cost visibility").
///
/// Two rules run through all of it. Every quantity is measured from a stored row, never modelled:
/// streaming hours come from session timestamps, egress from the bytes each relay actually sent,
/// AI spend from the tokens Phase 6 recorded per job. And anything the platform does not measure is
/// named as unmetered rather than reported as zero — an invisible cost line is worse than an absent
/// one, because nobody budgets for it.
/// </summary>
public sealed class UsageService(
    IAppDbContext db,
    IClock clock,
    WorkspaceAuthorizationService authorization,
    TenantLimitService tenantLimits,
    IOptions<CostOptions> costs,
    IOptions<RuntimeOptions> runtime)
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>Longest window a usage report may cover, so one request cannot scan years.</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    private readonly CostOptions _costs = costs.Value;

    public async Task<WorkspaceUsageResponse> GetWorkspaceUsageAsync(Guid workspaceId, Guid userId,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.AnalyticsView,
            cancellationToken);

        var now = clock.UtcNow;
        var periodEnd = to ?? now;
        var periodStart = from ?? periodEnd.AddDays(-30);

        if (periodStart >= periodEnd)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The start of the period must precede its end.");
        }

        if (periodEnd - periodStart > MaxPeriod)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A usage period may cover at most 366 days.");
        }

        var limits = await tenantLimits.ResolveAsync(workspaceId, cancellationToken);

        var sessionsInPeriod = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.CreatedAt >= periodStart && s.CreatedAt <= periodEnd)
            .Select(s => new { s.Id, s.StartedAt, s.EndedAt })
            .ToListAsync(cancellationToken);

        var sessionIds = sessionsInPeriod.Select(s => s.Id).ToList();

        // A session still on air counts up to now, so today's usage is not silently zero.
        var streamingHours = sessionsInPeriod.Sum(s => Hours(s.StartedAt, s.EndedAt ?? now));

        var ingestBytes = await db.LiveSessionHealth
            .AsNoTracking()
            .Where(h => sessionIds.Contains(h.LiveSessionId))
            .SumAsync(h => (long?)h.BytesReceived, cancellationToken) ?? 0L;

        var relays = await db.StreamDestinations
            .AsNoTracking()
            .Where(d => sessionIds.Contains(d.LiveSessionId))
            .Select(d => new { d.StartedAt, d.StoppedAt, d.BytesSent })
            .ToListAsync(cancellationToken);

        var relayHours = relays.Sum(r => Hours(r.StartedAt, r.StoppedAt ?? now));
        var relayEgressBytes = relays.Sum(r => r.BytesSent);

        // Storage is what is held now, not what was written during the period: it is a standing
        // cost, and the retention window is what ends it.
        var stored = await db.Recordings
            .AsNoTracking()
            .Where(r => r.Status == RecordingStatus.Ready
                        && db.LiveSessions.Any(s => s.Id == r.LiveSessionId && s.WorkspaceId == workspaceId))
            .Select(r => r.SizeBytes)
            .ToListAsync(cancellationToken);

        var storedBytes = stored.Sum(size => size ?? 0L);

        var aiJobs = await db.AiJobs
            .AsNoTracking()
            .Where(j => sessionIds.Contains(j.LiveSessionId) && j.Status == AiJobStatus.Succeeded)
            .Select(j => new { j.ModelId, j.InputTokens, j.OutputTokens })
            .ToListAsync(cancellationToken);

        var aiCost = aiJobs
            .Select(job => AiPricing.EstimateUsd(job.ModelId, job.InputTokens, job.OutputTokens))
            .Where(cost => cost is not null)
            .Sum(cost => cost!.Value);

        var openSessions = await db.LiveSessions
            .AsNoTracking()
            .CountAsync(s => s.WorkspaceId == workspaceId
                             && s.Status != LiveSessionStatus.Ended
                             && s.Status != LiveSessionStatus.Failed, cancellationToken);

        var broadcasting = await db.LiveSessions
            .AsNoTracking()
            .CountAsync(s => s.WorkspaceId == workspaceId
                             && (s.Status == LiveSessionStatus.Live
                                 || s.Status == LiveSessionStatus.Degraded
                                 || s.Status == LiveSessionStatus.Reconnecting), cancellationToken);

        var usage = new WorkspaceUsageTotals(
            Sessions: sessionsInPeriod.Count,
            StreamingHours: Round(streamingHours),
            RelayHours: Round(relayHours),
            IngestGb: Round(ingestBytes / BytesPerGb, 3),
            RelayEgressGb: Round(relayEgressBytes / BytesPerGb, 3),
            StoredRecordingGb: Round(storedBytes / BytesPerGb, 3),
            RecordingsStored: stored.Count,
            AiJobs: aiJobs.Count);

        return new WorkspaceUsageResponse(
            workspaceId,
            limits.Plan.ToString().ToUpperInvariant(),
            periodStart,
            periodEnd,
            new WorkspaceCapacityResponse(
                openSessions,
                broadcasting,
                limits.MaxConcurrentSessions,
                limits.MaxDestinationsPerSession,
                limits.MaxSourcesPerSession,
                limits.RecordingRetentionDays,
                limits.ResidencyRegion),
            usage,
            BuildCost(usage, aiCost));
    }

    /// <summary>
    /// Platform-wide capacity for an operator. Deliberately status counts and queue depths rather
    /// than per-tenant detail: this answers "can this deployment take more load", not "what is that
    /// customer doing".
    /// </summary>
    public async Task<PlatformCapacityResponse> GetPlatformCapacityAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var byStatus = await db.LiveSessions
            .AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var sessionsByStatus = Enum.GetValues<LiveSessionStatus>()
            .ToDictionary(
                status => status.ToString().ToUpperInvariant(),
                status => byStatus.FirstOrDefault(row => row.Status == status)?.Count ?? 0);

        var broadcasting = await db.LiveSessions
            .AsNoTracking()
            .CountAsync(s => s.Status == LiveSessionStatus.Live
                             || s.Status == LiveSessionStatus.Degraded
                             || s.Status == LiveSessionStatus.Reconnecting, cancellationToken);

        var workspacesBroadcasting = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.Status == LiveSessionStatus.Live
                        || s.Status == LiveSessionStatus.Degraded
                        || s.Status == LiveSessionStatus.Reconnecting)
            .Select(s => s.WorkspaceId)
            .Distinct()
            .CountAsync(cancellationToken);

        var connectedSources = await db.SessionSources
            .AsNoTracking()
            .CountAsync(s => s.Status == SourceStatus.Connected, cancellationToken);

        var liveDestinations = await db.StreamDestinations
            .AsNoTracking()
            .CountAsync(d => d.Status == DestinationStatus.Live, cancellationToken);

        var queued = await db.AiJobs.AsNoTracking().CountAsync(j => j.Status == AiJobStatus.Queued, cancellationToken);
        var running = await db.AiJobs.AsNoTracking().CountAsync(j => j.Status == AiJobStatus.Running, cancellationToken);

        var oldestQueuedAt = await db.AiJobs
            .AsNoTracking()
            .Where(j => j.Status == AiJobStatus.Queued)
            .OrderBy(j => j.RequestedAt)
            .Select(j => (DateTimeOffset?)j.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var readyRecordings = await db.Recordings
            .AsNoTracking()
            .Where(r => r.Status == RecordingStatus.Ready)
            .Select(r => new { r.SizeBytes, r.ExpiresAt })
            .ToListAsync(cancellationToken);

        var leases = await db.RuntimeLeases.AsNoTracking().ToListAsync(cancellationToken);
        var workspaces = await db.Workspaces.AsNoTracking().CountAsync(cancellationToken);

        return new PlatformCapacityResponse(
            runtime.Value.InstanceId,
            runtime.Value.Role.ToString().ToUpperInvariant(),
            WorkspaceLimits.NormalizeRegion(runtime.Value.Region),
            now,
            sessionsByStatus,
            broadcasting,
            connectedSources,
            liveDestinations,
            workspaces,
            workspacesBroadcasting,
            new AiQueueStatusResponse(queued, running,
                oldestQueuedAt is null ? null : Round((now - oldestQueuedAt.Value).TotalSeconds, 1)),
            new RecordingCapacityResponse(
                readyRecordings.Count,
                Round(readyRecordings.Sum(r => r.SizeBytes ?? 0L) / BytesPerGb, 3),
                readyRecordings.Count(r => r.ExpiresAt is not null && r.ExpiresAt <= now.AddHours(24)),
                readyRecordings.Count(r => r.ExpiresAt is not null && r.ExpiresAt <= now)),
            leases
                .OrderBy(lease => lease.Name, StringComparer.Ordinal)
                .Select(lease => new LeaseStatusResponse(lease.Name, lease.OwnerId, lease.ExpiresAt,
                    lease.FencingToken, lease.ExpiresAt > now))
                .ToList());
    }

    private WorkspaceCostResponse BuildCost(WorkspaceUsageTotals usage, decimal aiCost)
    {
        var lines = new List<CostLine>
        {
            Line("streaming", "Broadcast hours", usage.StreamingHours, "hour", _costs.StreamingPerHour),
            Line("relay", "Platform relay hours", usage.RelayHours, "hour", _costs.DestinationRelayPerHour),
            Line("egress", "Relay egress", usage.RelayEgressGb, "GB", _costs.EgressPerGb),
            Line("storage", "Recording storage", usage.StoredRecordingGb, "GB-month",
                _costs.RecordingStoragePerGbMonth),

            // AI is not an estimate against a configured rate: Phase 6 records the tokens each job
            // actually used, and this line is priced from those.
            new("ai", "AI analysis", usage.AiJobs, "job", null, decimal.Round(aiCost, 4)),
        };

        var priced = lines.Where(line => line.Amount is not null).Select(line => line.Amount!.Value).ToList();

        return new WorkspaceCostResponse(
            _costs.Currency,
            _costs.IsConfigured,
            lines,
            priced.Count > 0 ? decimal.Round(priced.Sum(), 4) : null,
            NotMetered);
    }

    /// <summary>
    /// Named rather than reported as zero. Viewer delivery is the important one: nothing in this
    /// platform meters bytes per viewer, so a report showing 0 GB would be a figure an operator
    /// would budget against.
    /// </summary>
    private static readonly string[] NotMetered =
    [
        "Viewer delivery — CDN and WebRTC egress to viewers is not metered by this platform",
        "Transcoding and packaging compute beyond the broadcast hours above",
        "Support, platform overhead, and anything billed outside this deployment",
    ];

    private static CostLine Line(string key, string label, double quantity, string unit, decimal? rate) =>
        new(key, label, quantity, unit, rate,
            rate is null ? null : decimal.Round(rate.Value * (decimal)quantity, 4));

    private static double Hours(DateTimeOffset? from, DateTimeOffset? to) =>
        from is null || to is null || to <= from ? 0d : (to.Value - from.Value).TotalHours;

    private static double Round(double value, int digits = 2) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}

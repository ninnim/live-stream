using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Governance;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Governance;

/// <summary>
/// Tenant administration: limits, plans, data export, and erasure
/// (implementation/phase-7: "Advanced tenant limits", "Compliance/retention controls").
/// </summary>
public sealed class WorkspaceGovernanceService(
    IAppDbContext db,
    IRecordingStore recordingStore,
    WorkspaceAuthorizationService authorization,
    TenantLimitService tenantLimits,
    RetentionService retention,
    IClock clock,
    IOptions<GovernanceOptions> governance,
    IOptions<RuntimeOptions> runtime,
    ILogger<WorkspaceGovernanceService> logger)
{
    private readonly GovernanceOptions _governance = governance.Value;

    public async Task<WorkspaceLimitsResponse> GetLimitsAsync(Guid workspaceId, Guid userId,
        CancellationToken cancellationToken)
    {
        // Deliberately readable by anyone who can see the workspace's sessions: a producer hitting
        // a limit needs to be able to see what the limit is.
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.LiveSessionView,
            cancellationToken);

        return Project(await tenantLimits.GetAsync(workspaceId, cancellationToken));
    }

    public async Task<WorkspaceLimitsResponse> UpdateLimitsAsync(Guid workspaceId, Guid userId,
        UpdateWorkspaceLimitsRequest request, CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var limits = await tenantLimits.GetOrCreateAsync(workspaceId, cancellationToken);

        limits.ReplaceOverrides(
            request.MaxConcurrentSessions,
            request.MaxDestinationsPerSession,
            request.MaxSourcesPerSession,
            request.RecordingRetentionDays,
            request.ResidencyRegion,
            clock.UtcNow);

        await db.SaveChangesAsync(cancellationToken);

        // Saving the policy is what applies it to recordings already on disk, including any that
        // predate retention entirely.
        await retention.ReapplyAsync(workspaceId, limits.EffectiveRecordingRetentionDays, cancellationToken);

        logger.LogInformation(
            "Workspace limits updated {WorkspaceId} plan={Plan} retentionDays={RetentionDays} residency={Residency}",
            workspaceId, limits.Plan, limits.EffectiveRecordingRetentionDays, limits.ResidencyRegion ?? "any");

        return Project(limits);
    }

    /// <summary>
    /// Moves a workspace to another plan. Operator-only, and reached through the internal
    /// operations API rather than a member's token: a plan is what the customer is entitled to, and
    /// nobody inside the tenant may raise their own entitlement.
    /// </summary>
    public async Task<WorkspaceLimitsResponse> ChangePlanAsync(Guid workspaceId, string plan,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<WorkspacePlan>(plan, ignoreCase: true, out var parsed))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"Unknown plan '{plan}'. Expected one of {string.Join(", ", Enum.GetNames<WorkspacePlan>())}.");
        }

        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken))
        {
            throw new DomainException(ErrorCodes.WorkspaceNotFound, "Workspace not found.");
        }

        var limits = await tenantLimits.GetOrCreateAsync(workspaceId, cancellationToken);
        var previous = limits.Plan;
        limits.ChangePlan(parsed, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        await retention.ReapplyAsync(workspaceId, limits.EffectiveRecordingRetentionDays, cancellationToken);

        logger.LogInformation("Workspace plan changed {WorkspaceId} from={Previous} to={Plan}",
            workspaceId, previous, parsed);

        return Project(limits);
    }

    /// <summary>
    /// Everything this workspace holds, as one document
    /// (implementation/phase-7: "Compliance/retention controls").
    ///
    /// It carries no secret: no stream keys, no client secrets, no device tokens, no password
    /// hashes. An export is a file that ends up in an inbox, and the platform's secrets must not
    /// travel with it.
    /// </summary>
    public async Task<WorkspaceExportResponse> ExportAsync(Guid workspaceId, Guid userId,
        CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken)
                        ?? throw new DomainException(ErrorCodes.WorkspaceNotFound, "Workspace not found.");

        var limits = await tenantLimits.GetAsync(workspaceId, cancellationToken);

        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .Join(db.Users, m => m.UserId, u => u.Id,
                (m, u) => new { u.Email, u.DisplayName, m.Role, m.CreatedAt })
            .OrderBy(m => m.Email)
            .ToListAsync(cancellationToken);

        // Projected into the response shape in memory: constructing a record inside the query
        // is not something the provider can translate.
        var exportedMembers = members
            .Select(m => new ExportedMember(m.Email, m.DisplayName, m.Role.ToString().ToUpperInvariant(),
                m.CreatedAt))
            .ToList();

        var total = await db.LiveSessions.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId, cancellationToken);

        var sessions = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(_governance.ExportMaxSessions)
            .Select(s => new
            {
                s.Id, s.Title, s.Description, s.Status, s.Visibility, s.CreatedAt, s.StartedAt, s.EndedAt,
            })
            .ToListAsync(cancellationToken);

        var sessionIds = sessions.Select(s => s.Id).ToList();

        var events = await db.LiveSessionEvents
            .AsNoTracking()
            .Where(e => sessionIds.Contains(e.LiveSessionId))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.LiveSessionId, e.CreatedAt, e.Type, e.Detail, e.ErrorCode })
            .ToListAsync(cancellationToken);

        var recordings = await db.Recordings
            .AsNoTracking()
            .Where(r => sessionIds.Contains(r.LiveSessionId))
            .Select(r => new
            {
                r.LiveSessionId, r.Id, r.Status, r.DurationSeconds, r.SizeBytes, r.StartedAt, r.EndedAt,
                r.ExpiresAt, r.DeletedAt,
            })
            .ToListAsync(cancellationToken);

        // Selected column by column, and neither StreamKeyCipher nor ResolvedStreamKeyCipher is
        // among them. Projecting the entity would have carried both into the file.
        var destinations = await db.StreamDestinations
            .AsNoTracking()
            .Where(d => sessionIds.Contains(d.LiveSessionId))
            .Select(d => new
            {
                d.LiveSessionId, d.Provider, d.DisplayName, d.Status, d.StartedAt, d.StoppedAt, d.BytesSent,
            })
            .ToListAsync(cancellationToken);

        var exported = sessions.Select(session => new ExportedSession(
                session.Id,
                session.Title,
                session.Description,
                session.Status.ToString().ToUpperInvariant(),
                session.Visibility.ToString().ToUpperInvariant(),
                session.CreatedAt,
                session.StartedAt,
                session.EndedAt,
                session.StartedAt is not null && session.EndedAt is not null
                    ? (int)(session.EndedAt.Value - session.StartedAt.Value).TotalSeconds
                    : null,
                events.Where(e => e.LiveSessionId == session.Id)
                    .Select(e => new ExportedEvent(e.CreatedAt, e.Type.ToString(), e.Detail, e.ErrorCode))
                    .ToList(),
                recordings.Where(r => r.LiveSessionId == session.Id)
                    .Select(r => new ExportedRecording(r.Id, r.Status.ToString().ToUpperInvariant(),
                        r.DurationSeconds, r.SizeBytes, r.StartedAt, r.EndedAt, r.ExpiresAt, r.DeletedAt))
                    .ToList(),
                destinations.Where(d => d.LiveSessionId == session.Id)
                    .Select(d => new ExportedDestination(d.Provider.ToString().ToUpperInvariant(), d.DisplayName,
                        d.Status.ToString().ToUpperInvariant(), d.StartedAt, d.StoppedAt, d.BytesSent))
                    .ToList()))
            .ToList();

        logger.LogInformation("Workspace exported {WorkspaceId} sessions={Sessions} truncated={Truncated}",
            workspaceId, exported.Count, total > exported.Count);

        return new WorkspaceExportResponse(
            clock.UtcNow,
            workspaceId,
            workspace.Name,
            limits.Plan.ToString().ToUpperInvariant(),
            total > exported.Count,
            exportedMembers,
            exported);
    }

    /// <summary>
    /// Erases a workspace: its media first, then its rows.
    ///
    /// Media before rows on purpose. If the rows went first and media deletion then failed, the
    /// platform would be left with files it no longer has any record of — and no way to find them
    /// again. This order can only leave the opposite: a row whose media is already gone, which the
    /// next attempt cleans up.
    ///
    /// The caller must repeat the workspace name back. Nothing here is recoverable.
    /// </summary>
    public async Task<WorkspaceErasureResponse> EraseAsync(Guid workspaceId, Guid userId, string? confirmation,
        CancellationToken cancellationToken)
    {
        await authorization.EnsureAllowedAsync(workspaceId, userId, WorkspacePermission.WorkspaceManage,
            cancellationToken);

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken)
                        ?? throw new DomainException(ErrorCodes.WorkspaceNotFound, "Workspace not found.");

        if (!string.Equals(confirmation?.Trim(), workspace.Name, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Type the workspace name exactly to confirm. This cannot be undone.");
        }

        var broadcasting = await db.LiveSessions
            .AsNoTracking()
            .CountAsync(s => s.WorkspaceId == workspaceId
                             && (s.Status == LiveSessionStatus.Live
                                 || s.Status == LiveSessionStatus.Degraded
                                 || s.Status == LiveSessionStatus.Reconnecting
                                 || s.Status == LiveSessionStatus.Starting
                                 || s.Status == LiveSessionStatus.Stopping), cancellationToken);

        if (broadcasting > 0)
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "This workspace still has a session on air. End it before erasing the workspace.");
        }

        var sessions = await db.LiveSessions.Where(s => s.WorkspaceId == workspaceId).ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(s => s.Id).ToList();

        var recordings = await db.Recordings
            .Where(r => sessionIds.Contains(r.LiveSessionId))
            .ToListAsync(cancellationToken);

        var bytesFreed = 0L;

        foreach (var recording in recordings.Where(r => r.Status != RecordingStatus.Deleted))
        {
            bytesFreed += await recordingStore.DeleteAsync(recording.StorageKey, cancellationToken);
        }

        // Sessions cascade to health, events, recordings, sources, scenes, branding, destinations
        // and AI jobs; the workspace cascades to its members, limits and SSO connection. Live
        // sessions hold no foreign key to the workspace, so they are removed explicitly.
        db.LiveSessions.RemoveRange(sessions);
        db.Workspaces.Remove(workspace);
        await db.SaveChangesAsync(cancellationToken);

        var completedAt = clock.UtcNow;

        logger.LogInformation(
            "Workspace erased {WorkspaceId} sessions={Sessions} recordings={Recordings} bytesFreed={Bytes}",
            workspaceId, sessions.Count, recordings.Count, bytesFreed);

        return new WorkspaceErasureResponse(workspaceId, sessions.Count, recordings.Count, bytesFreed, completedAt);
    }

    private WorkspaceLimitsResponse Project(WorkspaceLimits limits)
    {
        var effective = tenantLimits.Resolve(limits);
        var allowance = limits.Allowance;

        return new WorkspaceLimitsResponse(
            limits.WorkspaceId,
            limits.Plan.ToString().ToUpperInvariant(),
            effective.MaxConcurrentSessions,
            effective.MaxDestinationsPerSession,
            effective.MaxSourcesPerSession,
            effective.RecordingRetentionDays,
            allowance.MaxConcurrentSessions,
            allowance.MaxDestinationsPerSession,
            allowance.MaxSourcesPerSession,
            allowance.RecordingRetentionDays,
            limits.MaxConcurrentSessionsOverride,
            limits.MaxDestinationsPerSessionOverride,
            limits.MaxSourcesPerSessionOverride,
            limits.RecordingRetentionDaysOverride,
            limits.ResidencyRegion,
            WorkspaceLimits.NormalizeRegion(runtime.Value.Region),
            allowance.SingleSignOn,
            allowance.DataResidency,
            limits.UpdatedAt);
    }
}

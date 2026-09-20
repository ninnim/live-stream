using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Domain.Recordings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Governance;

public sealed record RetentionSweepResult(int Deleted, long BytesFreed, int Failed);

/// <summary>
/// Deletes recordings whose workspace retention window has elapsed
/// (implementation/phase-7: "Compliance/retention controls").
///
/// Expiry is stored on the recording rather than recomputed here, so the sweep is one indexed
/// query however many workspaces exist, and so a viewer can be shown the date their recording goes
/// away. The cost is that changing a policy has to rewrite the recordings it covers — which
/// <see cref="ReapplyAsync"/> does, and which is a rare admin action rather than an hourly one.
///
/// A recording whose media cannot be deleted is left alone and retried next sweep. Marking it
/// Deleted anyway would erase the platform's only record of media that still exists.
/// </summary>
public sealed class RetentionService(
    IAppDbContext db,
    IRecordingStore store,
    IClock clock,
    IOptions<GovernanceOptions> options,
    ILogger<RetentionService> logger)
{
    private readonly GovernanceOptions _options = options.Value;

    public async Task<RetentionSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        if (!_options.RetentionEnabled)
        {
            return new RetentionSweepResult(0, 0, 0);
        }

        var now = clock.UtcNow;

        var expired = await db.Recordings
            .Where(r => r.Status == RecordingStatus.Ready && r.ExpiresAt != null && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Take(_options.RetentionBatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return new RetentionSweepResult(0, 0, 0);
        }

        var deleted = 0;
        var failed = 0;
        var bytesFreed = 0L;

        foreach (var recording in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bytesFreed += await store.DeleteAsync(recording.StorageKey, cancellationToken);

                recording.Status = RecordingStatus.Deleted;
                recording.DeletedAt = clock.UtcNow;
                recording.UpdatedAt = recording.DeletedAt.Value;
                deleted += 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left Ready on purpose: the media is still there, and the row must keep saying so.
                failed += 1;
                logger.LogError(ex, "Retention could not delete recording {RecordingId} key={StorageKey}",
                    recording.Id, recording.StorageKey);
            }
        }

        if (deleted > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Retention deleted {Deleted} recordings, freed {Bytes} bytes, {Failed} failed",
                deleted, bytesFreed, failed);
        }

        return new RetentionSweepResult(deleted, bytesFreed, failed);
    }

    /// <summary>
    /// Recomputes expiry for one workspace's recordings after its policy changed. Returns how many
    /// rows moved.
    ///
    /// This also covers recordings finalized before retention existed, whose expiry is null: the
    /// upgrade itself does not schedule a back catalogue for deletion, and saving the policy is
    /// the deliberate act that opts it in.
    /// </summary>
    public async Task<int> ReapplyAsync(Guid workspaceId, int retentionDays, CancellationToken cancellationToken)
    {
        var recordings = await db.Recordings
            .Where(r => r.Status == RecordingStatus.Ready
                        && r.EndedAt != null
                        && db.LiveSessions.Any(s => s.Id == r.LiveSessionId && s.WorkspaceId == workspaceId))
            .ToListAsync(cancellationToken);

        var changed = 0;

        foreach (var recording in recordings)
        {
            var expiresAt = recording.EndedAt!.Value.AddDays(retentionDays);

            if (recording.ExpiresAt == expiresAt)
            {
                continue;
            }

            recording.ExpiresAt = expiresAt;
            recording.UpdatedAt = clock.UtcNow;
            changed += 1;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Retention policy applied to {Count} recordings in workspace {WorkspaceId}",
                changed, workspaceId);
        }

        return changed;
    }
}

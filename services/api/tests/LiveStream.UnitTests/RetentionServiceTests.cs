using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using LiveStream.Infrastructure.Persistence;
using LiveStream.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Recording retention (implementation/phase-7: "Compliance/retention controls").
///
/// A retention policy that is only written down is not a retention policy. These tests are about
/// the bytes actually going, and about the two ways that could go wrong: deleting media that is
/// still inside its window, and recording a deletion that did not happen.
/// </summary>
public class RetentionServiceTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly TestClock _clock = new();
    private readonly FakeRecordingStore _store = new() { Stored = new StoredRecording(1024, 4, null, null) };

    [Fact]
    public async Task An_expired_recording_has_its_media_deleted_and_is_marked_deleted()
    {
        var recordingId = await SeedRecordingAsync(expiresAt: _clock.UtcNow.AddMinutes(-1));

        var result = await SweepAsync();

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1024, result.BytesFreed);
        Assert.Contains("path-0", _store.Deleted);

        var recording = await ReadAsync(recordingId);
        Assert.Equal(RecordingStatus.Deleted, recording.Status);
        Assert.Equal(_clock.UtcNow, recording.DeletedAt);
    }

    [Fact]
    public async Task A_recording_still_inside_its_window_is_left_alone()
    {
        var recordingId = await SeedRecordingAsync(expiresAt: _clock.UtcNow.AddDays(1));

        Assert.Equal(0, (await SweepAsync()).Deleted);

        Assert.Equal(RecordingStatus.Ready, (await ReadAsync(recordingId)).Status);
        Assert.Empty(_store.Deleted);
    }

    [Fact]
    public async Task A_recording_with_no_expiry_is_never_swept()
    {
        // Recordings finalized before retention existed. An upgrade must not schedule a customer's
        // back catalogue for deletion on its own.
        var recordingId = await SeedRecordingAsync(expiresAt: null);

        Assert.Equal(0, (await SweepAsync()).Deleted);
        Assert.Equal(RecordingStatus.Ready, (await ReadAsync(recordingId)).Status);
    }

    [Fact]
    public async Task Media_that_could_not_be_deleted_leaves_the_recording_alone()
    {
        // The row is the platform's only record that the media exists. Marking it Deleted anyway
        // would lose track of files that are still on disk.
        var recordingId = await SeedRecordingAsync(expiresAt: _clock.UtcNow.AddMinutes(-1));
        _store.ThrowOnDelete = new IOException("storage unavailable");

        var result = await SweepAsync();

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);
        Assert.Equal(RecordingStatus.Ready, (await ReadAsync(recordingId)).Status);
    }

    [Fact]
    public async Task A_sweep_deletes_no_more_than_its_batch_size()
    {
        // Tightening a policy from a year to a week must drain steadily rather than hammer storage.
        for (var i = 0; i < 5; i++)
        {
            await SeedRecordingAsync(expiresAt: _clock.UtcNow.AddMinutes(-1), index: i);
        }

        Assert.Equal(2, (await SweepAsync(batchSize: 2)).Deleted);
        Assert.Equal(2, (await SweepAsync(batchSize: 2)).Deleted);
        Assert.Equal(1, (await SweepAsync(batchSize: 2)).Deleted);
        Assert.Equal(0, (await SweepAsync(batchSize: 2)).Deleted);
    }

    [Fact]
    public async Task Retention_switched_off_deletes_nothing()
    {
        var recordingId = await SeedRecordingAsync(expiresAt: _clock.UtcNow.AddMinutes(-1));

        Assert.Equal(0, (await SweepAsync(enabled: false)).Deleted);
        Assert.Equal(RecordingStatus.Ready, (await ReadAsync(recordingId)).Status);
    }

    [Fact]
    public async Task Applying_a_policy_recomputes_expiry_including_recordings_that_had_none()
    {
        var workspaceId = Guid.NewGuid();
        var recordingId = await SeedRecordingAsync(expiresAt: null, workspaceId: workspaceId);

        await using (var context = _database.CreateContext())
        {
            var changed = await Service(context).ReapplyAsync(workspaceId, retentionDays: 7, default);
            Assert.Equal(1, changed);
        }

        var recording = await ReadAsync(recordingId);
        Assert.Equal(recording.EndedAt!.Value.AddDays(7), recording.ExpiresAt);
    }

    [Fact]
    public async Task Applying_a_policy_leaves_another_workspace_untouched()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await SeedRecordingAsync(expiresAt: null, workspaceId: mine);
        var otherId = await SeedRecordingAsync(expiresAt: null, workspaceId: theirs, index: 1);

        await using (var context = _database.CreateContext())
        {
            await Service(context).ReapplyAsync(mine, retentionDays: 7, default);
        }

        Assert.Null((await ReadAsync(otherId)).ExpiresAt);
    }

    private async Task<RetentionSweepResult> SweepAsync(int batchSize = 100, bool enabled = true)
    {
        await using var context = _database.CreateContext();
        return await Service(context, batchSize, enabled).SweepAsync(default);
    }

    private RetentionService Service(AppDbContext context, int batchSize = 100, bool enabled = true) =>
        new(context, _store, _clock,
            Options.Create(new GovernanceOptions { RetentionBatchSize = batchSize, RetentionEnabled = enabled }),
            NullLogger<RetentionService>.Instance);

    private async Task<Guid> SeedRecordingAsync(DateTimeOffset? expiresAt, Guid? workspaceId = null, int index = 0)
    {
        await using var context = _database.CreateContext();

        var session = LiveSession.Create(workspaceId ?? Guid.NewGuid(), Guid.NewGuid(), $"Show {index}", null,
            LiveSessionVisibility.Private, recordingEnabled: true, _clock.UtcNow);

        var recording = new Recording
        {
            LiveSessionId = session.Id,
            StorageKey = $"path-{index}",
            Status = RecordingStatus.Ready,
            SizeBytes = 1024,
            StartedAt = _clock.UtcNow.AddHours(-2),
            EndedAt = _clock.UtcNow.AddHours(-1),
            ExpiresAt = expiresAt,
            CreatedAt = _clock.UtcNow.AddHours(-2),
            UpdatedAt = _clock.UtcNow.AddHours(-1),
        };

        context.LiveSessions.Add(session);
        context.Recordings.Add(recording);
        await context.SaveChangesAsync();

        return recording.Id;
    }

    private async Task<Recording> ReadAsync(Guid recordingId)
    {
        await using var context = _database.CreateContext();
        return await context.Recordings.AsNoTracking().SingleAsync(r => r.Id == recordingId);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();
}

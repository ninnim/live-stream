using LiveStream.Domain.Sessions;
using LiveStream.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Persistence behaviour of the Live Session aggregate against a real relational database.
/// </summary>
public class LiveSessionPersistenceTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Transitions_on_a_reloaded_session_persist_their_events()
    {
        // Regression guard: events are appended through the aggregate's collection. If their keys
        // are pre-assigned, change tracking treats them as existing rows and every transition after
        // a reload fails to save — which would break the entire lifecycle after the first request.
        var sessionId = await SeedDraftSessionAsync();

        await using (var context = _database.CreateContext())
        {
            var session = await context.LiveSessions.Include(s => s.Health).SingleAsync(s => s.Id == sessionId);
            session.TransitionTo(LiveSessionStatus.Preparing, Now.AddMinutes(1));
            session.TransitionTo(LiveSessionStatus.Ready, Now.AddMinutes(2));
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var events = await verify.LiveSessionEvents
            .Where(e => e.LiveSessionId == sessionId)
            .ToListAsync();

        Assert.Contains(events, e => e.ToStatus == LiveSessionStatus.Preparing);
        Assert.Contains(events, e => e.ToStatus == LiveSessionStatus.Ready);
        Assert.All(events, e => Assert.NotEqual(Guid.Empty, e.Id));
    }

    [Fact]
    public async Task Concurrent_updates_to_the_same_session_are_rejected()
    {
        // Two devices racing start/stop must not silently overwrite one another.
        var sessionId = await SeedDraftSessionAsync();

        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();

        var firstCopy = await first.LiveSessions.Include(s => s.Health).SingleAsync(s => s.Id == sessionId);
        var secondCopy = await second.LiveSessions.Include(s => s.Health).SingleAsync(s => s.Id == sessionId);

        firstCopy.TransitionTo(LiveSessionStatus.Preparing, Now.AddMinutes(1));
        await first.SaveChangesAsync();

        secondCopy.TransitionTo(LiveSessionStatus.Preparing, Now.AddMinutes(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task The_version_token_advances_on_each_saved_change()
    {
        var sessionId = await SeedDraftSessionAsync();

        await using (var context = _database.CreateContext())
        {
            var session = await context.LiveSessions.Include(s => s.Health).SingleAsync(s => s.Id == sessionId);
            Assert.Equal(0, session.Version);

            session.TransitionTo(LiveSessionStatus.Preparing, Now.AddMinutes(1));
            await context.SaveChangesAsync();
            Assert.Equal(1, session.Version);
        }

        await using var verify = _database.CreateContext();
        var reloaded = await verify.LiveSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task The_media_path_is_unique_across_sessions()
    {
        var sessionId = await SeedDraftSessionAsync();

        await using var context = _database.CreateContext();
        var existing = await context.LiveSessions.SingleAsync(s => s.Id == sessionId);

        var duplicate = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Duplicate path", null,
            LiveSessionVisibility.Private, false, Now);

        // Force a collision to prove the unique index is actually enforced.
        typeof(LiveSession).GetProperty(nameof(LiveSession.MediaPathName))!
            .SetValue(duplicate, existing.MediaPathName);

        context.LiveSessions.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_events_and_health()
    {
        var sessionId = await SeedDraftSessionAsync();

        await using (var context = _database.CreateContext())
        {
            var session = await context.LiveSessions.SingleAsync(s => s.Id == sessionId);
            context.LiveSessions.Remove(session);
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        Assert.Equal(0, await verify.LiveSessionEvents.CountAsync(e => e.LiveSessionId == sessionId));
        Assert.Equal(0, await verify.LiveSessionHealth.CountAsync(h => h.LiveSessionId == sessionId));
    }

    private async Task<Guid> SeedDraftSessionAsync()
    {
        await using var context = _database.CreateContext();
        var session = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Persistence test", null,
            LiveSessionVisibility.Private, false, Now);

        context.LiveSessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();
}

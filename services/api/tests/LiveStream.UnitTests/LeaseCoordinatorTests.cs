using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Domain.Governance;
using LiveStream.Infrastructure.Persistence;
using LiveStream.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Leader election for background work (implementation/phase-7: independent scaling).
///
/// These run against a real relational database rather than a stub, because the property being
/// tested <em>is</em> the database's: two instances writing the same row, one of them losing.
/// </summary>
public class LeaseCoordinatorTests : IAsyncDisposable
{
    private const string Lease = RuntimeLease.Names.StreamHealth;

    private readonly SqliteTestDatabase _database = new();
    private readonly TestClock _clock = new();

    [Fact]
    public async Task Only_one_instance_wins_a_lease_nobody_holds()
    {
        await using var contextA = _database.CreateContext();
        await using var contextB = _database.CreateContext();

        var a = Coordinator(contextA, "instance-a");
        var b = Coordinator(contextB, "instance-b");

        var wonByA = await a.TryAcquireAsync(Lease, default);
        var wonByB = await b.TryAcquireAsync(Lease, default);

        Assert.True(wonByA);
        Assert.False(wonByB);
    }

    [Fact]
    public async Task The_holder_renews_without_a_takeover()
    {
        await using var context = _database.CreateContext();
        var a = Coordinator(context, "instance-a");

        Assert.True(await a.TryAcquireAsync(Lease, default));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await a.TryAcquireAsync(Lease, default));

        var lease = await ReadAsync();
        Assert.Equal("instance-a", lease.OwnerId);

        // The fencing token moves only on a takeover, so a renewal leaving it alone is what makes
        // a takeover detectable at all.
        Assert.Equal(1, lease.FencingToken);
    }

    [Fact]
    public async Task A_lease_whose_holder_stopped_renewing_is_taken_over()
    {
        await using var contextA = _database.CreateContext();
        Assert.True(await Coordinator(contextA, "instance-a").TryAcquireAsync(Lease, default));

        // Longer than the lease TTL: instance A is gone, and the work must not stay parked forever.
        _clock.Advance(TimeSpan.FromSeconds(31));

        await using var contextB = _database.CreateContext();
        Assert.True(await Coordinator(contextB, "instance-b").TryAcquireAsync(Lease, default));

        var lease = await ReadAsync();
        Assert.Equal("instance-b", lease.OwnerId);
        Assert.Equal(2, lease.FencingToken);
    }

    [Fact]
    public async Task Two_instances_racing_for_an_expired_lease_produce_one_winner()
    {
        await using var seed = _database.CreateContext();
        Assert.True(await Coordinator(seed, "instance-a").TryAcquireAsync(Lease, default));

        _clock.Advance(TimeSpan.FromSeconds(31));

        // Both contexts read the row before either writes, which is the race the version token
        // exists to settle. EF keeps the first-read values on a tracked entity, so the second
        // write really does carry a stale precondition rather than a refreshed one.
        await using var contextB = _database.CreateContext();
        await using var contextC = _database.CreateContext();

        await contextB.RuntimeLeases.FirstOrDefaultAsync(l => l.Name == Lease);
        await contextC.RuntimeLeases.FirstOrDefaultAsync(l => l.Name == Lease);

        var b = Coordinator(contextB, "instance-b");
        var c = Coordinator(contextC, "instance-c");

        var wonByB = await b.TryAcquireAsync(Lease, default);
        var wonByC = await c.TryAcquireAsync(Lease, default);

        Assert.True(wonByB);
        Assert.False(wonByC);
        Assert.Equal("instance-b", (await ReadAsync()).OwnerId);
    }

    [Fact]
    public async Task Releasing_hands_the_work_over_immediately()
    {
        // This is what makes a rolling deploy quick: without it the next instance waits out the
        // whole TTL before anything reconciles.
        await using var contextA = _database.CreateContext();
        var a = Coordinator(contextA, "instance-a");
        Assert.True(await a.TryAcquireAsync(Lease, default));

        await a.ReleaseAsync(Lease, default);

        await using var contextB = _database.CreateContext();
        Assert.True(await Coordinator(contextB, "instance-b").TryAcquireAsync(Lease, default));
    }

    [Fact]
    public async Task Releasing_a_lease_held_by_somebody_else_does_nothing()
    {
        await using var contextA = _database.CreateContext();
        Assert.True(await Coordinator(contextA, "instance-a").TryAcquireAsync(Lease, default));

        await using var contextB = _database.CreateContext();
        await Coordinator(contextB, "instance-b").ReleaseAsync(Lease, default);

        var lease = await ReadAsync();
        Assert.Equal("instance-a", lease.OwnerId);
        Assert.True(lease.ExpiresAt > _clock.UtcNow);
    }

    [Fact]
    public async Task Different_loops_hold_different_leases()
    {
        // One lease per loop, so background work spreads across worker instances instead of piling
        // onto whichever one happens to be leader.
        await using var contextA = _database.CreateContext();
        await using var contextB = _database.CreateContext();

        Assert.True(await Coordinator(contextA, "instance-a").TryAcquireAsync(RuntimeLease.Names.StreamHealth, default));
        Assert.True(await Coordinator(contextB, "instance-b").TryAcquireAsync(RuntimeLease.Names.AiJobs, default));
    }

    [Fact]
    public async Task The_single_instance_coordinator_always_runs()
    {
        var coordinator = new SingleInstanceLeaseCoordinator(
            Options.Create(new RuntimeOptions { InstanceId = "solo", LeaderElection = false }));

        Assert.True(await coordinator.TryAcquireAsync(Lease, default));
        Assert.True(await coordinator.TryAcquireAsync(Lease, default));

        await using var context = _database.CreateContext();
        Assert.Empty(await context.RuntimeLeases.ToListAsync());
    }

    private DatabaseLeaseCoordinator Coordinator(AppDbContext context, string instanceId) =>
        new(context, _clock,
            Options.Create(new RuntimeOptions { InstanceId = instanceId, LeaseTtlSeconds = 30 }),
            NullLogger<DatabaseLeaseCoordinator>.Instance);

    private async Task<RuntimeLease> ReadAsync()
    {
        await using var context = _database.CreateContext();
        return await context.RuntimeLeases.AsNoTracking().SingleAsync(l => l.Name == Lease);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();
}

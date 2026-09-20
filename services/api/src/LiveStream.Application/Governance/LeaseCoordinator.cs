using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Domain.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Governance;

/// <summary>
/// Decides which instance runs a named background job.
/// </summary>
public interface ILeaseCoordinator
{
    /// <summary>Identifier this instance claims leases under. Appears in logs and in the lease row.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Claims or renews the lease. Returns false when another instance holds it, in which case the
    /// caller must skip this pass rather than fail.
    /// </summary>
    Task<bool> TryAcquireAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Gives up a lease this instance holds, so a shutting-down replica hands work over in seconds
    /// rather than after the lease expires. Never throws: it runs on the shutdown path.
    /// </summary>
    Task ReleaseAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Leases held in the platform's own database.
///
/// The database is the only component every replica already depends on, so using it here adds no
/// new failure domain — where a dedicated lock service (etcd, Redis) would add one, and would be
/// one more thing to run for a platform that deliberately deferred Redis
/// (docs/decisions/0004-redis-deferred.md).
///
/// Every path is optimistic: read, decide, write with the version as a precondition. Losing the
/// write means another instance got there first, which is a normal outcome and not an error.
/// </summary>
public sealed class DatabaseLeaseCoordinator(
    IAppDbContext db,
    IClock clock,
    IOptions<RuntimeOptions> options,
    ILogger<DatabaseLeaseCoordinator> logger) : ILeaseCoordinator
{
    private readonly RuntimeOptions _options = options.Value;

    public string InstanceId => _options.InstanceId;

    public async Task<bool> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var lease = await db.RuntimeLeases.FirstOrDefaultAsync(l => l.Name == name, cancellationToken);

        if (lease is null)
        {
            return await CreateAsync(name, now, cancellationToken);
        }

        var isMine = lease.OwnerId == InstanceId;

        if (!isMine && lease.ExpiresAt > now)
        {
            return false; // Someone else holds it and it has not lapsed.
        }

        if (!isMine)
        {
            lease.OwnerId = InstanceId;
            lease.AcquiredAt = now;
            lease.FencingToken += 1;
        }

        lease.ExpiresAt = now + _options.LeaseTtl;
        lease.Version += 1;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another instance wrote the row between the read and the write. It owns the work now.
            return false;
        }

        if (!isMine)
        {
            logger.LogInformation("Lease acquired {Lease} owner={InstanceId} fencingToken={FencingToken}",
                name, InstanceId, lease.FencingToken);
        }

        return true;
    }

    public async Task ReleaseAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await db.RuntimeLeases
                .FirstOrDefaultAsync(l => l.Name == name && l.OwnerId == InstanceId, cancellationToken);

            if (lease is null)
            {
                return;
            }

            // Expiring it rather than deleting the row keeps the fencing token's history, and the
            // next instance takes over on its very next tick.
            lease.ExpiresAt = clock.UtcNow;
            lease.Version += 1;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Lease released {Lease} owner={InstanceId}", name, InstanceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Shutdown must not fail because a lease could not be handed back: it lapses on its own.
            logger.LogWarning(ex, "Releasing lease {Lease} failed; it will expire instead", name);
        }
    }

    private async Task<bool> CreateAsync(string name, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = new RuntimeLease
        {
            Name = name,
            OwnerId = InstanceId,
            AcquiredAt = now,
            ExpiresAt = now + _options.LeaseTtl,
            FencingToken = 1,
            Version = 1,
        };

        db.RuntimeLeases.Add(lease);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Lease created {Lease} owner={InstanceId}", name, InstanceId);
            return true;
        }
        catch (DbUpdateException)
        {
            // Two instances started at once and both found no row. The primary key settles it.
            return false;
        }
    }
}

/// <summary>
/// Runs every pass, for deployments that run exactly one instance of the background work — a
/// single container, or a worker deployment scaled to one. Saves a database write per tick.
/// </summary>
public sealed class SingleInstanceLeaseCoordinator(IOptions<RuntimeOptions> options) : ILeaseCoordinator
{
    public string InstanceId => options.Value.InstanceId;

    public Task<bool> TryAcquireAsync(string name, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task ReleaseAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
}

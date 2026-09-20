using System.Text.Json;
using LiveStream.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LiveStream.Infrastructure.Auth;

/// <summary>
/// Sign-in state backed by <see cref="IDistributedCache"/> — in memory for a single instance,
/// Redis-backed when one is configured, with no code change
/// (docs/decisions/0004-redis-deferred.md).
///
/// One consequence is worth stating plainly: with the in-memory default and more than one API
/// replica, a sign-in that starts on one replica and comes back on another finds no state and
/// fails. A multi-replica deployment therefore needs either a shared cache or sticky sessions —
/// recorded in docs/decisions/0015 and in the deployment guide.
/// </summary>
public sealed class DistributedCacheSsoLoginStateStore(IDistributedCache cache) : ISsoLoginStateStore
{
    private const string KeyPrefix = "sso-login:";

    public async Task StoreAsync(string state, SsoLoginState entry, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        await cache.SetAsync(KeyPrefix + state, JsonSerializer.SerializeToUtf8Bytes(entry),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime },
            cancellationToken);
    }

    public async Task<SsoLoginState?> ConsumeAsync(string state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var key = KeyPrefix + state;
        var payload = await cache.GetAsync(key, cancellationToken);

        if (payload is null)
        {
            return null;
        }

        // Removed before it is returned, so a code cannot be replayed even if what follows throws.
        await cache.RemoveAsync(key, cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<SsoLoginState>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

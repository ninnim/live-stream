using System.Text.Json;
using LiveStream.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// OAuth state backed by <see cref="IDistributedCache"/>.
///
/// Memory-backed in a single-instance deployment and Redis-backed when one is configured, with no
/// code change — the same trade-off recorded in docs/decisions/0004-redis-deferred.md. State is
/// short-lived and losing it on restart only means the operator restarts the linking flow, so this
/// is one of the few places where an in-memory default is genuinely acceptable.
/// </summary>
public sealed class DistributedCacheOAuthStateStore(IDistributedCache cache) : IOAuthStateStore
{
    private const string KeyPrefix = "oauth-state:";

    public async Task StoreAsync(string state, OAuthStateEntry entry, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);

        await cache.SetAsync(KeyPrefix + state, payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime },
            cancellationToken);
    }

    public async Task<OAuthStateEntry?> ConsumeAsync(string state, CancellationToken cancellationToken)
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

        // Removed before it is returned, so a replayed callback finds nothing even if the caller
        // fails partway through. Single use is the property that makes state worth having.
        await cache.RemoveAsync(key, cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<OAuthStateEntry>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

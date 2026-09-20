using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;

namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// Resolves provider adapters by <see cref="DestinationProvider"/>.
///
/// Built once from the registered adapters, so adding a platform is a registration change and
/// nothing else — no switch statement anywhere in the application layer grows a new case.
/// </summary>
public sealed class DestinationProviderRegistry : IDestinationProviderRegistry
{
    private readonly IReadOnlyDictionary<DestinationProvider, IDestinationProviderAdapter> _adapters;
    private readonly IReadOnlyDictionary<DestinationProvider, IProviderOAuthClient> _oauthClients;

    public DestinationProviderRegistry(IEnumerable<IDestinationProviderAdapter> adapters)
    {
        var materialized = adapters.ToList();

        var duplicates = materialized
            .GroupBy(a => a.Provider)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            // Two adapters for one provider would make behaviour depend on registration order.
            throw new InvalidOperationException(
                $"More than one destination adapter is registered for: {string.Join(", ", duplicates)}.");
        }

        _adapters = materialized.ToDictionary(a => a.Provider);

        // An adapter that also implements the OAuth contract is discovered here rather than
        // registered twice, which keeps the two capabilities from drifting apart.
        _oauthClients = materialized
            .OfType<IProviderOAuthClient>()
            .ToDictionary(client => client.Provider);
    }

    public IDestinationProviderAdapter Get(DestinationProvider provider) =>
        _adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new DomainException(ErrorCodes.ValidationFailed,
                $"No adapter is registered for {provider}.");

    public bool TryGetOAuthClient(DestinationProvider provider, out IProviderOAuthClient client) =>
        _oauthClients.TryGetValue(provider, out client!);

    public IReadOnlyList<DestinationProviderDescriptor> DescribeAll() =>
        _adapters.Values
            .Select(adapter => adapter.Describe())
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
            .ToList();
}

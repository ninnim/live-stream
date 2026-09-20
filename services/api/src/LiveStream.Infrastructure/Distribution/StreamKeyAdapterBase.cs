using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;

namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// Shared behaviour for destinations that publish with an operator-supplied stream key.
///
/// Resolution is the identity function: the operator already told us the endpoint and the key, so
/// there is no provider call to make and nothing that can fail at start. That is exactly why
/// stream-key destinations are the dependable path — they work without any provider API access,
/// app review, or account eligibility.
/// </summary>
public abstract class StreamKeyAdapterBase : IDestinationProviderAdapter
{
    public abstract DestinationProvider Provider { get; }

    public abstract DestinationProviderDescriptor Describe();

    public virtual Task<DestinationTargetResolution> ResolveTargetAsync(DestinationResolveContext context,
        CancellationToken cancellationToken)
    {
        if (context.Destination.CredentialMode != DestinationCredentialMode.StreamKey)
        {
            return Task.FromResult(new DestinationTargetResolution(false, null, null, null, null,
                ErrorCodes.ValidationFailed,
                $"{Describe().DisplayName} destinations must be configured with a stream key.",
                Retryable: false));
        }

        if (string.IsNullOrWhiteSpace(context.StreamKey) || string.IsNullOrWhiteSpace(context.Destination.IngestUrl))
        {
            return Task.FromResult(new DestinationTargetResolution(false, null, null, null, null,
                ErrorCodes.ValidationFailed,
                "This destination is missing its ingest URL or stream key.",
                Retryable: false));
        }

        return Task.FromResult(new DestinationTargetResolution(
            true,
            context.Destination.IngestUrl,
            context.StreamKey,
            ExternalBroadcastId: null,
            WatchUrl: null));
    }

    /// <summary>
    /// Nothing to tell the platform: an RTMP endpoint goes live when bytes arrive and ends when they
    /// stop. Providers with an explicit broadcast lifecycle override this.
    /// </summary>
    public virtual Task<ProviderOperationResult> OnBroadcastLiveAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken) => Task.FromResult(ProviderOperationResult.NotApplicable);

    public virtual Task<ProviderOperationResult> OnBroadcastEndedAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken) => Task.FromResult(ProviderOperationResult.NotApplicable);
}

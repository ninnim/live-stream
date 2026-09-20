using LiveStream.Domain.Distribution;

namespace LiveStream.Application.Abstractions;

/// <summary>
/// Everything platform-specific about one external destination.
///
/// docs/06-multi-platform-distribution.md: "Keep provider adapters isolated and configurable. Never
/// hardcode provider-specific assumptions into the session domain model." This interface is that
/// boundary — the orchestrator below it knows only about resolving a target and being told when the
/// broadcast starts and stops.
/// </summary>
public interface IDestinationProviderAdapter
{
    DestinationProvider Provider { get; }

    /// <summary>
    /// Static description used by the UI to render the right form and help text.
    /// Pure and side-effect free so it can be served without touching the provider.
    /// </summary>
    DestinationProviderDescriptor Describe();

    /// <summary>
    /// Produces the RTMP endpoint and key to publish to for this run.
    ///
    /// For a pasted stream key this simply returns what the operator supplied. For a linked account
    /// it calls the provider API to create a broadcast, which is why it can fail and must be able to
    /// report a normalized error.
    /// </summary>
    Task<DestinationTargetResolution> ResolveTargetAsync(DestinationResolveContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called once the platform has accepted media. Providers that require an explicit transition —
    /// YouTube will not show a broadcast as live until its status is advanced — do it here.
    /// Failure is reported, never thrown: the relay is already running and the session is unaffected.
    /// </summary>
    Task<ProviderOperationResult> OnBroadcastLiveAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when the destination stops, for any reason. Providers that leave a broadcast dangling
    /// otherwise (again, YouTube) close it out here. Must be safe to call more than once.
    /// </summary>
    Task<ProviderOperationResult> OnBroadcastEndedAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// OAuth capability, implemented only by providers that support linked accounts.
/// Split from <see cref="IDestinationProviderAdapter"/> so a custom RTMP endpoint is not forced to
/// implement six methods that make no sense for it.
/// </summary>
public interface IProviderOAuthClient
{
    DestinationProvider Provider { get; }

    /// <summary>True when this deployment has credentials configured for the provider.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Where to send the operator to grant consent. <paramref name="state"/> is an opaque,
    /// single-use value the caller must verify on the way back, to prevent CSRF on account linking.
    /// </summary>
    Uri BuildAuthorizationUrl(string state, string redirectUri);

    /// <summary>Exchanges an authorization code for tokens.</summary>
    Task<ProviderTokenSet> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Exchanges a refresh token for a new access token.</summary>
    Task<ProviderTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Identifies the account the tokens belong to, so it can be named in the UI.</summary>
    Task<ProviderAccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>Best-effort revocation at the provider. Local erasure happens regardless.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>Resolves adapters by provider. Registered as a singleton over the adapter collection.</summary>
public interface IDestinationProviderRegistry
{
    IDestinationProviderAdapter Get(DestinationProvider provider);

    bool TryGetOAuthClient(DestinationProvider provider, out IProviderOAuthClient client);

    IReadOnlyList<DestinationProviderDescriptor> DescribeAll();
}

/// <summary>UI-facing description of a provider. Contains no secrets and is safe to serve publicly.</summary>
public sealed record DestinationProviderDescriptor(
    DestinationProvider Provider,
    string DisplayName,
    bool SupportsStreamKey,
    bool SupportsLinkedAccount,
    // True when linked accounts are configured in this deployment, not merely supported.
    bool LinkedAccountConfigured,
    // Prefilled RTMP endpoint, where the platform publishes a stable one.
    string? DefaultIngestUrl,
    // One sentence telling the operator where to find their stream key.
    string StreamKeyHelp,
    // Documentation URL for obtaining credentials.
    string? HelpUrl);

public sealed record DestinationResolveContext(
    StreamDestination Destination,
    string SessionTitle,
    string? SessionDescription,
    bool SessionIsPublic,
    // Decrypted stream key for stream-key destinations; null for linked accounts.
    string? StreamKey,
    // Decrypted access token for linked accounts; null otherwise.
    string? AccessToken);

/// <summary>
/// The endpoint to publish to. <see cref="StreamKey"/> is plaintext and is encrypted by the caller
/// before it touches the database.
/// </summary>
public sealed record DestinationTargetResolution(
    bool Success,
    string? IngestUrl,
    string? StreamKey,
    string? ExternalBroadcastId,
    string? WatchUrl,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    // False when retrying cannot help — bad credentials, ineligible account.
    bool Retryable = false);

public sealed record DestinationLifecycleContext(
    StreamDestination Destination,
    string? AccessToken);

public sealed record ProviderOperationResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static readonly ProviderOperationResult Ok = new(true);

    /// <summary>For adapters with nothing to do at this point in the lifecycle.</summary>
    public static readonly ProviderOperationResult NotApplicable = new(true);
}

/// <summary>Tokens as returned by a provider. Plaintext; encrypted immediately by the caller.</summary>
public sealed record ProviderTokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string Scopes);

public sealed record ProviderAccountProfile(string ExternalAccountId, string DisplayName);

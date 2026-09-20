namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// Per-provider OAuth client credentials, bound from <c>Distribution:Providers</c>.
///
/// Absent by default. A deployment with no credentials for a provider still offers it as a
/// stream-key destination — account linking is simply reported as unconfigured, rather than the
/// operator being sent to a consent screen that cannot work.
///
/// These are client secrets: supply them through environment variables or a secret store, never in
/// committed configuration (docs/11-security.md).
/// </summary>
public sealed class ProvidersOptions
{
    public const string SectionName = "Distribution:Providers";

    public ProviderCredentialOptions YouTube { get; set; } = new();

    public ProviderCredentialOptions Facebook { get; set; } = new();

    public ProviderCredentialOptions TikTok { get; set; } = new();

    public ProviderCredentialOptions Twitch { get; set; } = new();
}

public sealed class ProviderCredentialOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// Space-separated scopes to request. Defaults are set per adapter; override only to narrow
    /// them, since widening beyond what the provider app is approved for fails at consent time.
    /// </summary>
    public string? Scopes { get; set; }

    /// <summary>True only when both halves of the client credential are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

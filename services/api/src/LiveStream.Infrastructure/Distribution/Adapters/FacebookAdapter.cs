using System.Net.Http.Json;
using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure.Distribution.Adapters;

/// <summary>
/// Facebook Live, in both supported modes.
///
/// With a stream key the operator pastes the server URL and key from Facebook Live Producer. With a
/// linked account it creates a live video through the Graph API and uses the ingest address that
/// comes back, so each session becomes its own post.
///
/// Live publishing through the API requires the <c>publish_video</c> permission, which Meta grants
/// only after app review. This adapter is complete and correct against the documented API, but a
/// deployment whose Meta app has not been approved will receive a permission error from Facebook at
/// the point of creating the live video — the stream-key mode is unaffected and needs no review.
/// </summary>
public sealed class FacebookAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<ProvidersOptions> providers,
    ILogger<FacebookAdapter> logger)
    : StreamKeyAdapterBase, IProviderOAuthClient
{
    public const string HttpClientName = "facebook";

    private const string GraphVersion = "v21.0";
    private const string GraphBase = $"https://graph.facebook.com/{GraphVersion}";
    private const string AuthorizeEndpoint = $"https://www.facebook.com/{GraphVersion}/dialog/oauth";
    private const string DefaultScopes = "publish_video";

    private ProviderCredentialOptions Credentials => providers.Value.Facebook;

    public override DestinationProvider Provider => DestinationProvider.Facebook;

    DestinationProvider IProviderOAuthClient.Provider => DestinationProvider.Facebook;

    public bool IsConfigured => Credentials.IsConfigured;

    public override DestinationProviderDescriptor Describe() => new(
        DestinationProvider.Facebook,
        "Facebook",
        SupportsStreamKey: true,
        SupportsLinkedAccount: true,
        LinkedAccountConfigured: IsConfigured,
        DefaultIngestUrl: "rtmps://live-api-s.facebook.com:443/rtmp",
        StreamKeyHelp: "Facebook Live Producer, then Streaming software. Copy the stream key. Or connect your account to create a live video automatically for every session.",
        HelpUrl: "https://www.facebook.com/live/producer");

    // -----------------------------------------------------------------------------------------
    // Target resolution
    // -----------------------------------------------------------------------------------------

    public override async Task<DestinationTargetResolution> ResolveTargetAsync(DestinationResolveContext context,
        CancellationToken cancellationToken)
    {
        if (context.Destination.CredentialMode == DestinationCredentialMode.StreamKey)
        {
            return await base.ResolveTargetAsync(context, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(context.AccessToken))
        {
            return Failure(ErrorCodes.ProviderAccountUnavailable,
                "The Facebook account needs to be connected again.", retryable: false);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        var payload = new Dictionary<string, string>
        {
            ["status"] = "LIVE_NOW",
            ["title"] = Truncate(context.SessionTitle, 255),
            ["access_token"] = context.AccessToken,
        };

        if (!string.IsNullOrWhiteSpace(context.SessionDescription))
        {
            payload["description"] = Truncate(context.SessionDescription, 5000);
        }

        try
        {
            using var content = new FormUrlEncodedContent(payload);
            using var response = await client.PostAsync($"{GraphBase}/me/live_videos", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
                var (errorCode, retryable) = ProviderHttp.Classify(response.StatusCode);

                logger.LogWarning("Facebook live video creation failed: {Status} {Message}",
                    (int)response.StatusCode, message);

                return Failure(errorCode, $"Facebook could not start the live video: {message}", retryable);
            }

            var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken);
            if (json is not { } element)
            {
                return Failure(ErrorCodes.ProviderApiError, "Facebook returned no live video details.",
                    retryable: true);
            }

            var id = ProviderHttp.GetString(element, "id");

            // Prefer the TLS address. Facebook returns both, and there is no reason to publish a
            // stream key over plaintext RTMP when RTMPS is offered.
            var streamUrl = ProviderHttp.GetString(element, "secure_stream_url")
                            ?? ProviderHttp.GetString(element, "stream_url");

            if (id is null || streamUrl is null)
            {
                return Failure(ErrorCodes.ProviderApiError, "Facebook returned an incomplete ingest address.",
                    retryable: true);
            }

            // Facebook hands back one URL with the key already appended; the relay needs them apart
            // so the key can be kept out of logs on both sides.
            if (!TrySplitStreamUrl(streamUrl, out var ingestUrl, out var streamKey))
            {
                return Failure(ErrorCodes.ProviderApiError,
                    "Facebook returned an ingest address in an unexpected format.", retryable: false);
            }

            var permalink = ProviderHttp.GetString(element, "permalink_url");

            logger.LogInformation("Facebook live video created destination={DestinationId} videoId={VideoId}",
                context.Destination.Id, id);

            return new DestinationTargetResolution(
                true,
                ingestUrl,
                streamKey,
                id,
                permalink is null ? null : $"https://www.facebook.com{permalink}");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Facebook API unreachable for destination {DestinationId}", context.Destination.Id);
            return Failure(ErrorCodes.ProviderApiError, "Facebook could not be reached.", retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ErrorCodes.ProviderApiError, "Facebook did not respond in time.", retryable: true);
        }
    }

    /// <summary>
    /// Splits <c>rtmps://host:443/rtmp/KEY</c> into the endpoint and the key.
    /// Everything after the final path segment is the key.
    /// </summary>
    public static bool TrySplitStreamUrl(string streamUrl, out string ingestUrl, out string streamKey)
    {
        ingestUrl = string.Empty;
        streamKey = string.Empty;

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // A query string carries the key on some Facebook responses; keep it attached to the key so
        // the relay reassembles the exact URL Facebook issued.
        var pathAndQuery = uri.PathAndQuery;
        var lastSlash = pathAndQuery.LastIndexOf('/');

        if (lastSlash <= 0 || lastSlash == pathAndQuery.Length - 1)
        {
            return false;
        }

        var basePath = pathAndQuery[..lastSlash];
        streamKey = pathAndQuery[(lastSlash + 1)..];

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        ingestUrl = $"{uri.Scheme}://{authority}{basePath}";

        return streamKey.Length > 0;
    }

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    public override async Task<ProviderOperationResult> OnBroadcastEndedAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken)
    {
        if (context.Destination.CredentialMode != DestinationCredentialMode.LinkedAccount
            || context.Destination.ExternalBroadcastId is not { } videoId
            || string.IsNullOrWhiteSpace(context.AccessToken))
        {
            return ProviderOperationResult.NotApplicable;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["end_live_video"] = "true",
                ["access_token"] = context.AccessToken,
            });

            using var response = await client.PostAsync($"{GraphBase}/{videoId}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return ProviderOperationResult.Ok;
            }

            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            logger.LogInformation("Ending Facebook live video {VideoId} returned {Status}: {Message}",
                videoId, (int)response.StatusCode, message);

            return new ProviderOperationResult(false, ErrorCodes.ProviderApiError, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ending Facebook live video {VideoId} failed", videoId);
            return new ProviderOperationResult(false, ErrorCodes.ProviderApiError, "Facebook could not be reached.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // OAuth
    // -----------------------------------------------------------------------------------------

    public Uri BuildAuthorizationUrl(string state, string redirectUri)
    {
        var scopes = string.IsNullOrWhiteSpace(Credentials.Scopes) ? DefaultScopes : Credentials.Scopes;

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = Credentials.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state,
        };

        var encoded = string.Join('&', query
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

        return new Uri($"{AuthorizeEndpoint}?{encoded}");
    }

    public async Task<ProviderTokenSet> ExchangeCodeAsync(string code, string redirectUri,
        CancellationToken cancellationToken)
    {
        var shortLived = await GetTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = Credentials.ClientId!,
            ["client_secret"] = Credentials.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
        }, cancellationToken);

        // Facebook issues a short-lived token from a code. Exchanging it immediately for the
        // long-lived one is what keeps a linked account usable for more than a couple of hours;
        // Facebook has no refresh token, so this exchange is the whole renewal story.
        return await ExchangeForLongLivedAsync(shortLived.AccessToken, cancellationToken);
    }

    public Task<ProviderTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        // The stored "refresh token" is the long-lived access token; re-exchanging it extends it.
        ExchangeForLongLivedAsync(refreshToken, cancellationToken);

    private async Task<ProviderTokenSet> ExchangeForLongLivedAsync(string accessToken,
        CancellationToken cancellationToken) =>
        await GetTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "fb_exchange_token",
            ["client_id"] = Credentials.ClientId!,
            ["client_secret"] = Credentials.ClientSecret!,
            ["fb_exchange_token"] = accessToken,
        }, cancellationToken);

    public async Task<ProviderAccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            $"{GraphBase}/me?fields=id,name&access_token={Uri.EscapeDataString(accessToken)}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            throw new DomainException(ErrorCodes.ProviderApiError, $"Facebook rejected the request: {message}");
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken)
                   ?? throw new DomainException(ErrorCodes.ProviderApiError, "Facebook returned an empty profile.");

        var id = ProviderHttp.GetString(json, "id")
                 ?? throw new DomainException(ErrorCodes.ProviderApiError, "Facebook returned no account id.");

        return new ProviderAccountProfile(id, ProviderHttp.GetString(json, "name") ?? "Facebook account");
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.DeleteAsync(
            $"{GraphBase}/me/permissions?access_token={Uri.EscapeDataString(refreshToken)}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Facebook permission revocation returned {Status}", (int)response.StatusCode);
        }
    }

    private async Task<ProviderTokenSet> GetTokenAsync(Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        var encoded = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var response = await client.GetAsync($"{GraphBase}/oauth/access_token?{encoded}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            var (errorCode, _) = ProviderHttp.Classify(response.StatusCode);
            throw new DomainException(errorCode, $"Facebook rejected the token request: {message}");
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken)
                   ?? throw new DomainException(ErrorCodes.ProviderApiError, "Facebook returned an empty token response.");

        var accessToken = ProviderHttp.GetString(json, "access_token")
                          ?? throw new DomainException(ErrorCodes.ProviderApiError, "Facebook returned no access token.");

        DateTimeOffset? expiresAt = json.TryGetProperty("expires_in", out var expiresIn)
                                    && expiresIn.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        // The long-lived access token doubles as the renewal credential, so it is stored in both
        // places. Facebook has no separate refresh token to keep.
        return new ProviderTokenSet(accessToken, accessToken, expiresAt, DefaultScopes);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static DestinationTargetResolution Failure(string errorCode, string message, bool retryable) =>
        new(false, null, null, null, null, errorCode, message, retryable);

    private static string Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

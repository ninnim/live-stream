using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure.Distribution.Adapters;

/// <summary>
/// YouTube Live, in both supported modes.
///
/// With a stream key the operator pastes their persistent key and this behaves like any other RTMP
/// destination. With a linked account it uses the YouTube Live Streaming API to create a broadcast
/// and a stream, bind them, and hand back the ingestion address — so each session appears as its own
/// YouTube broadcast with the right title and privacy, and no key is ever pasted.
///
/// Broadcasts are created with auto-start and auto-stop enabled. YouTube then transitions the
/// broadcast itself when media arrives, which is markedly more reliable than racing it with an
/// explicit transition call: the alternative fails whenever the transition is attempted a moment
/// before YouTube has accepted the first bytes.
/// </summary>
public sealed class YouTubeAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<ProvidersOptions> providers,
    ILogger<YouTubeAdapter> logger)
    : StreamKeyAdapterBase, IProviderOAuthClient
{
    public const string HttpClientName = "youtube";

    private const string ApiBase = "https://www.googleapis.com/youtube/v3";
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string DefaultScopes = "https://www.googleapis.com/auth/youtube";

    private ProviderCredentialOptions Credentials => providers.Value.YouTube;

    public override DestinationProvider Provider => DestinationProvider.YouTube;

    DestinationProvider IProviderOAuthClient.Provider => DestinationProvider.YouTube;

    public bool IsConfigured => Credentials.IsConfigured;

    public override DestinationProviderDescriptor Describe() => new(
        DestinationProvider.YouTube,
        "YouTube",
        SupportsStreamKey: true,
        SupportsLinkedAccount: true,
        LinkedAccountConfigured: IsConfigured,
        DefaultIngestUrl: "rtmp://a.rtmp.youtube.com/live2",
        StreamKeyHelp: "YouTube Studio, then Go Live, then Stream. Copy the stream key. Or connect your account to create a broadcast automatically for every session.",
        HelpUrl: "https://studio.youtube.com/channel/live");

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
                "The YouTube account needs to be connected again.", retryable: false);
        }

        using var client = CreateClient(context.AccessToken);

        try
        {
            var broadcast = await CreateBroadcastAsync(client, context, cancellationToken);
            if (broadcast.Failure is not null)
            {
                return broadcast.Failure;
            }

            var stream = await CreateStreamAsync(client, context, cancellationToken);
            if (stream.Failure is not null)
            {
                return stream.Failure;
            }

            var bind = await BindAsync(client, broadcast.Id!, stream.Id!, cancellationToken);
            if (bind is not null)
            {
                return bind;
            }

            logger.LogInformation(
                "YouTube broadcast created destination={DestinationId} broadcastId={BroadcastId}",
                context.Destination.Id, broadcast.Id);

            return new DestinationTargetResolution(
                true,
                stream.IngestionAddress,
                stream.StreamName,
                broadcast.Id,
                $"https://www.youtube.com/watch?v={broadcast.Id}");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "YouTube API unreachable for destination {DestinationId}", context.Destination.Id);
            return Failure(ErrorCodes.ProviderApiError, "YouTube could not be reached.", retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ErrorCodes.ProviderApiError, "YouTube did not respond in time.", retryable: true);
        }
    }

    private async Task<(string? Id, DestinationTargetResolution? Failure)> CreateBroadcastAsync(HttpClient client,
        DestinationResolveContext context, CancellationToken cancellationToken)
    {
        var payload = new
        {
            snippet = new
            {
                title = Truncate(context.SessionTitle, 100),
                description = Truncate(context.SessionDescription, 5000),
                // YouTube requires a scheduled start; "now" is correct for an immediate broadcast.
                scheduledStartTime = DateTimeOffset.UtcNow.ToString("o"),
            },
            status = new
            {
                privacyStatus = context.SessionIsPublic ? "public" : "unlisted",
                // Required by YouTube on every broadcast; false is the accurate answer for a
                // general-purpose platform and must not silently claim otherwise.
                selfDeclaredMadeForKids = false,
            },
            contentDetails = new
            {
                enableAutoStart = true,
                enableAutoStop = true,
            },
        };

        using var response = await client.PostAsJsonAsync(
            $"{ApiBase}/liveBroadcasts?part=snippet,status,contentDetails", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ToFailureAsync(response, "create a YouTube broadcast", cancellationToken));
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken);
        var id = json is { } element ? ProviderHttp.GetString(element, "id") : null;

        return id is null
            ? (null, Failure(ErrorCodes.ProviderApiError, "YouTube did not return a broadcast id.", retryable: true))
            : (id, null);
    }

    private async Task<(string? Id, string? IngestionAddress, string? StreamName, DestinationTargetResolution? Failure)>
        CreateStreamAsync(HttpClient client, DestinationResolveContext context, CancellationToken cancellationToken)
    {
        var payload = new
        {
            snippet = new { title = Truncate($"{context.SessionTitle} ingest", 128) },
            cdn = new
            {
                // "variable" lets the encoder decide, which is right when the source is a browser
                // whose resolution and frame rate depend on the camera it was given.
                frameRate = "variable",
                resolution = "variable",
                ingestionType = "rtmp",
            },
        };

        using var response = await client.PostAsJsonAsync(
            $"{ApiBase}/liveStreams?part=snippet,cdn,contentDetails", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, null, await ToFailureAsync(response, "create a YouTube stream", cancellationToken));
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken);
        if (json is not { } element)
        {
            return (null, null, null,
                Failure(ErrorCodes.ProviderApiError, "YouTube returned no stream details.", retryable: true));
        }

        var id = ProviderHttp.GetString(element, "id");
        var address = ProviderHttp.GetString(element, "cdn", "ingestionInfo", "ingestionAddress");
        var name = ProviderHttp.GetString(element, "cdn", "ingestionInfo", "streamName");

        if (id is null || address is null || name is null)
        {
            return (null, null, null,
                Failure(ErrorCodes.ProviderApiError, "YouTube returned an incomplete ingestion address.",
                    retryable: true));
        }

        return (id, address, name, null);
    }

    private async Task<DestinationTargetResolution?> BindAsync(HttpClient client, string broadcastId, string streamId,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            $"{ApiBase}/liveBroadcasts/bind?id={Uri.EscapeDataString(broadcastId)}" +
            $"&streamId={Uri.EscapeDataString(streamId)}&part=id,contentDetails",
            content: null, cancellationToken);

        return response.IsSuccessStatusCode
            ? null
            : await ToFailureAsync(response, "bind the YouTube broadcast to its stream", cancellationToken);
    }

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Nothing to do: the broadcast was created with auto-start, so YouTube transitions it when the
    /// first bytes arrive. Calling transition here would race that and fail with a status conflict.
    /// </summary>
    public override Task<ProviderOperationResult> OnBroadcastLiveAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken) => Task.FromResult(ProviderOperationResult.Ok);

    /// <summary>
    /// Completes the broadcast. Auto-stop covers the normal case, but an explicit transition closes
    /// out a broadcast whose media stopped without YouTube noticing, which otherwise sits open on
    /// the channel.
    /// </summary>
    public override async Task<ProviderOperationResult> OnBroadcastEndedAsync(DestinationLifecycleContext context,
        CancellationToken cancellationToken)
    {
        if (context.Destination.CredentialMode != DestinationCredentialMode.LinkedAccount
            || context.Destination.ExternalBroadcastId is not { } broadcastId
            || string.IsNullOrWhiteSpace(context.AccessToken))
        {
            return ProviderOperationResult.NotApplicable;
        }

        using var client = CreateClient(context.AccessToken);

        try
        {
            using var response = await client.PostAsync(
                $"{ApiBase}/liveBroadcasts/transition?broadcastStatus=complete" +
                $"&id={Uri.EscapeDataString(broadcastId)}&part=status",
                content: null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return ProviderOperationResult.Ok;
            }

            // A broadcast auto-stop already completed rejects this with a status conflict. That is
            // the desired end state, so it is not worth reporting as a failure.
            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            logger.LogInformation(
                "YouTube broadcast completion returned {Status} for {BroadcastId}: {Message}",
                (int)response.StatusCode, broadcastId, message);

            return new ProviderOperationResult(false, ErrorCodes.ProviderApiError, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Completing YouTube broadcast {BroadcastId} failed", broadcastId);
            return new ProviderOperationResult(false, ErrorCodes.ProviderApiError, "YouTube could not be reached.");
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
            // Both are required to receive a refresh token: Google issues one only on the first
            // consent unless consent is forced, and a destination that cannot refresh is a
            // destination that breaks silently a week later.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
        };

        var encoded = string.Join('&', query
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

        return new Uri($"{AuthorizeEndpoint}?{encoded}");
    }

    public async Task<ProviderTokenSet> ExchangeCodeAsync(string code, string redirectUri,
        CancellationToken cancellationToken) =>
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = Credentials.ClientId!,
            ["client_secret"] = Credentials.ClientSecret!,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, cancellationToken);

    public async Task<ProviderTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = Credentials.ClientId!,
            ["client_secret"] = Credentials.ClientSecret!,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, cancellationToken);

    public async Task<ProviderAccountProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessToken);
        using var response = await client.GetAsync($"{ApiBase}/channels?part=snippet&mine=true", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            throw new DomainException(ErrorCodes.ProviderApiError, $"YouTube rejected the request: {message}");
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken);

        if (json is not { } element
            || !element.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0)
        {
            // Google accounts without a YouTube channel authenticate successfully but have nothing
            // to broadcast to, so this needs to be a clear message rather than a null reference.
            throw new DomainException(ErrorCodes.ProviderAccountUnavailable,
                "That Google account has no YouTube channel. Create a channel first, then connect it.");
        }

        var channel = items[0];
        var id = ProviderHttp.GetString(channel, "id")
                 ?? throw new DomainException(ErrorCodes.ProviderApiError, "YouTube returned no channel id.");
        var title = ProviderHttp.GetString(channel, "snippet", "title") ?? "YouTube channel";

        return new ProviderAccountProfile(id, title);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
        using var response = await client.PostAsync(RevokeEndpoint, content, cancellationToken);

        // A token Google has already forgotten returns 400. The caller erases locally regardless.
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("YouTube token revocation returned {Status}", (int)response.StatusCode);
        }
    }

    private async Task<ProviderTokenSet> RequestTokenAsync(Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(TokenEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
            var (errorCode, _) = ProviderHttp.Classify(response.StatusCode);
            throw new DomainException(errorCode, $"Google rejected the token request: {message}");
        }

        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken)
                   ?? throw new DomainException(ErrorCodes.ProviderApiError, "Google returned an empty token response.");

        var accessToken = ProviderHttp.GetString(json, "access_token")
                          ?? throw new DomainException(ErrorCodes.ProviderApiError, "Google returned no access token.");

        var refreshToken = ProviderHttp.GetString(json, "refresh_token");
        var scopes = ProviderHttp.GetString(json, "scope") ?? DefaultScopes;

        DateTimeOffset? expiresAt = json.TryGetProperty("expires_in", out var expiresIn)
                                    && expiresIn.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        return new ProviderTokenSet(accessToken, refreshToken, expiresAt, scopes);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private HttpClient CreateClient(string accessToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<DestinationTargetResolution> ToFailureAsync(HttpResponseMessage response, string what,
        CancellationToken cancellationToken)
    {
        var message = await ProviderHttp.ReadErrorMessageAsync(response, cancellationToken);
        var (errorCode, retryable) = ProviderHttp.Classify(response.StatusCode);

        logger.LogWarning("YouTube failed to {What}: {Status} {Message}", what, (int)response.StatusCode, message);

        return Failure(errorCode, $"YouTube could not {what}: {message}", retryable);
    }

    private static DestinationTargetResolution Failure(string errorCode, string message, bool retryable) =>
        new(false, null, null, null, null, errorCode, message, retryable);

    private static string Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

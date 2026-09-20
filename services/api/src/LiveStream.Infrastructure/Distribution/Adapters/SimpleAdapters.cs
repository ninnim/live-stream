using LiveStream.Application.Abstractions;
using LiveStream.Domain.Distribution;

namespace LiveStream.Infrastructure.Distribution.Adapters;

/// <summary>
/// Any RTMP/RTMPS endpoint the operator supplies. No provider API exists, by definition.
///
/// This is the adapter that makes the destination model useful without any platform integration at
/// all — including for a second instance of this platform, a partner CDN, or a local test endpoint.
/// </summary>
public sealed class CustomRtmpAdapter : StreamKeyAdapterBase
{
    public override DestinationProvider Provider => DestinationProvider.CustomRtmp;

    public override DestinationProviderDescriptor Describe() => new(
        DestinationProvider.CustomRtmp,
        "Custom RTMP",
        SupportsStreamKey: true,
        SupportsLinkedAccount: false,
        LinkedAccountConfigured: false,
        DefaultIngestUrl: null,
        StreamKeyHelp: "Enter the RTMP or RTMPS server URL and stream key given to you by the service you are publishing to.",
        HelpUrl: null);
}

/// <summary>
/// Twitch. Publishing is stream-key only here on purpose: Twitch issues a stable key from the
/// creator dashboard, and its API adds nothing to the publish path that the key does not already do.
/// </summary>
public sealed class TwitchAdapter : StreamKeyAdapterBase
{
    public override DestinationProvider Provider => DestinationProvider.Twitch;

    public override DestinationProviderDescriptor Describe() => new(
        DestinationProvider.Twitch,
        "Twitch",
        SupportsStreamKey: true,
        SupportsLinkedAccount: false,
        LinkedAccountConfigured: false,
        // Twitch publishes a global ingest hostname that redirects to the nearest server.
        DefaultIngestUrl: "rtmp://live.twitch.tv/app",
        StreamKeyHelp: "Twitch Creator Dashboard, then Settings, then Stream. Copy the primary stream key.",
        HelpUrl: "https://dashboard.twitch.tv/settings/stream");
}

/// <summary>
/// TikTok Live.
///
/// Stream-key only. TikTok gates RTMP publishing behind account eligibility and gates its API behind
/// an approval process, so an adapter that assumed API access would fail for most operators. An
/// eligible account can paste a server URL and key from TikTok Live Studio, which works today —
/// which is what implementation/phase-2 means by "subject to current API/publishing eligibility".
/// </summary>
public sealed class TikTokAdapter : StreamKeyAdapterBase
{
    public override DestinationProvider Provider => DestinationProvider.TikTok;

    public override DestinationProviderDescriptor Describe() => new(
        DestinationProvider.TikTok,
        "TikTok",
        SupportsStreamKey: true,
        SupportsLinkedAccount: false,
        LinkedAccountConfigured: false,
        // TikTok issues a per-broadcast server URL, so there is no stable default to prefill.
        DefaultIngestUrl: null,
        StreamKeyHelp: "TikTok Live Studio, then Go Live, then choose streaming software. Copy the server URL and stream key. Live streaming requires an eligible account.",
        HelpUrl: "https://www.tiktok.com/live/creators");
}

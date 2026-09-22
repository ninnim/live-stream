using System.ComponentModel.DataAnnotations;

namespace LiveStream.Infrastructure.Media;

/// <summary>
/// Configuration for the MediaMTX media gateway (docs/decisions/0001-phase-1-media-gateway.md).
///
/// The <c>Control*</c> values are server-side only and must never be projected into an API
/// response: they address the gateway's private control API. The <c>Public*</c> values are the
/// browser-facing origins and carry no credentials.
/// </summary>
public sealed class MediaMtxOptions
{
    public const string SectionName = "Media:MediaMtx";

    /// <summary>Private control API base URL, e.g. <c>http://mediamtx:9997</c>. Never exposed to clients.</summary>
    [Required]
    public string ControlApiUrl { get; set; } = "http://localhost:9997";

    /// <summary>Public WebRTC origin used for WHIP ingest and WHEP playback, e.g. <c>http://localhost:8889</c>.</summary>
    [Required]
    public string PublicWebRtcUrl { get; set; } = "http://localhost:8889";

    /// <summary>Public HLS origin used for scalable playback, e.g. <c>http://localhost:8888</c>.</summary>
    [Required]
    public string PublicHlsUrl { get; set; } = "http://localhost:8888";

    /// <summary>
    /// Optional CDN or edge origin that fronts <see cref="PublicHlsUrl"/>, e.g.
    /// <c>https://cdn.example.com</c>. When set, HLS playback URLs point at it instead.
    ///
    /// Only HLS moves. HLS is segmented files over HTTP, which is what a CDN is for; WHEP is a
    /// negotiated peer connection to this gateway, and putting a caching layer in front of it
    /// would break it rather than speed it up.
    /// </summary>
    public string? PublicHlsCdnUrl { get; set; }

    /// <summary>
    /// Public RTMP origin for external encoders, e.g. <c>rtmp://stream.example.com:1935</c>.
    ///
    /// Unset by default, and unset means the feature is off: no stream key can be issued and the
    /// studio offers no encoder instructions. RTMP ingest is an open port that accepts a connection
    /// from anywhere before any credential is checked, so a deployment turns it on deliberately —
    /// here and in the gateway's own config — or not at all
    /// (docs/decisions/0022-external-encoder-ingest.md).
    /// </summary>
    public string? PublicRtmpUrl { get; set; }

    /// <summary>
    /// Public SRT origin for external encoders, e.g. <c>srt://stream.example.com:8890</c>.
    ///
    /// Same rule as <see cref="PublicRtmpUrl"/>: absent means off. SRT is offered alongside RTMP
    /// rather than instead of it because it survives a lossy mobile uplink far better, and a phone
    /// on cellular is the case this exists for.
    /// </summary>
    public string? PublicSrtUrl { get; set; }

    /// <summary>Timeout for control API calls. Kept short so a slow gateway cannot stall a request thread.</summary>
    [Range(1, 30)]
    public int ControlApiTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Optional credentials for the gateway control API, when it is protected. Server-side only.
    /// Supply through environment variables or a secret store, never in committed configuration.
    /// </summary>
    public string? ControlApiUser { get; set; }

    public string? ControlApiPassword { get; set; }
}

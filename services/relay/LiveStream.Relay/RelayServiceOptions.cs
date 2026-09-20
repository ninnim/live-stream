using System.ComponentModel.DataAnnotations;

namespace LiveStream.Relay;

public sealed class RelayServiceOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// Shared secret the control plane presents on every call. Defence in depth behind network
    /// isolation: a request to this service carries a stream key, so it must never be reachable
    /// from outside the internal network either.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Media gateway RTSP host and port, reachable only inside the internal network.</summary>
    [Required]
    public string SourceRtspBaseUrl { get; set; } = "rtsp://mediamtx:8554";

    /// <summary>Path to the encoder binary.</summary>
    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Path to the stream inspector, which ships with the encoder.
    ///
    /// Used to establish what the source carries before deciding what to send, because platforms
    /// reject a stream missing a kind without saying so. See <see cref="SourceTracks"/>.
    /// </summary>
    [Required]
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// How long to wait for the source inspection before giving up on it.
    ///
    /// RTSP answers with an SDP listing the tracks, so a publishing source answers in well under a
    /// second. This bound is for a source that is not publishing at all, where the encoder is about
    /// to fail and be retried anyway — so it only has to be short enough not to delay that.
    /// </summary>
    [Range(1, 60)]
    public int SourceInspectionTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// Whether to synthesise a missing track rather than send a stream without one.
    ///
    /// On by default, because the failure it prevents is invisible: Facebook Live accepts a
    /// video-only stream, counts it as live, and never shows it to anybody. Turn it off only to
    /// send exactly what the source carries.
    /// </summary>
    public bool SynthesizeMissingTracks { get; set; } = true;

    /// <summary>
    /// Video handling.
    ///
    /// <c>auto</c> picks the strongest encoder this machine can actually use, established by a trial
    /// encode at startup rather than by asking ffmpeg what it was built with — the stock build lists
    /// NVENC and Quick Sync on a machine with no GPU at all. Naming one explicitly overrides that,
    /// and is still trialled so a wrong choice is reported at startup rather than mid-broadcast.
    ///
    /// <c>copy</c> avoids re-encoding entirely and is correct only when the browser negotiated
    /// H.264, which the studio requests but cannot guarantee across every browser — see
    /// docs/decisions/0009. It also skips scaling and frame-rate normalisation, so a stream that
    /// needs either will be refused by the platform.
    /// </summary>
    [Required]
    [RegularExpression("^(auto|libx264|h264_nvenc|h264_qsv|copy)$")]
    public string VideoCodec { get; set; } = "auto";

    /// <summary>x264 speed/quality trade-off. Live encoding needs speed.</summary>
    [Required]
    public string VideoPreset { get; set; } = "veryfast";

    /// <summary>
    /// Target video bitrate when transcoding, in kbps.
    ///
    /// 6000 is sized for the hardest thing the studio can send — 1080p60 gameplay — where 2500,
    /// the old default, is visibly blocky whenever the picture moves. It is a ceiling as much as a
    /// target: x264 in ABR mode spends far less on a still slide.
    /// </summary>
    [Range(200, 20000)]
    public int VideoBitrateKbps { get; set; } = 6000;

    /// <summary>
    /// Largest frame the relay will send, in pixels.
    ///
    /// A shared screen arrives at whatever the display is — 1440p and 4K monitors are ordinary
    /// — and platforms do not accept arbitrary sizes. Facebook and YouTube both top out at
    /// 1920x1080 for RTMP ingest, and a stream above it is accepted and then not shown.
    /// </summary>
    [Range(320, 3840)]
    public int MaxVideoWidth { get; set; } = 1920;

    [Range(240, 2160)]
    public int MaxVideoHeight { get; set; } = 1080;

    /// <summary>
    /// Frame rate of the outgoing stream.
    ///
    /// Forced rather than passed through, because a WebRTC source does not have one: a shared
    /// screen produces frames only when something changes — sometimes under one per second — and
    /// platforms expect a steady constant-rate stream. Duplicating a frame costs a little bitrate;
    /// sending one frame per second costs the broadcast.
    ///
    /// 60 is the ceiling because it is the ceiling everywhere else. Browsers encode WebRTC video at
    /// up to 60, and RTMP ingest at YouTube, Facebook and Twitch tops out there too. Capture can be
    /// asked for 120 — nothing downstream will carry it.
    /// </summary>
    [Range(10, 60)]
    public int VideoFrameRate { get; set; } = 60;

    /// <summary>
    /// Audio bitrate in kbps. RTMP needs AAC, so audio is always transcoded from Opus.
    ///
    /// 160 rather than 128: this is a second lossy encode of audio that was already lossy over
    /// WebRTC, and generation loss is where a broadcast starts sounding like a phone call. The
    /// margin costs 32 kbps against a video stream measured in thousands.
    /// </summary>
    [Range(32, 512)]
    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>
    /// Audio sample rate sent to the platform.
    ///
    /// 48 kHz, which is what Opus carries natively and what every platform ingests. Resampling to
    /// 44.1 kHz — the old default — threw away the top of the band and spent CPU doing it, for no
    /// reason other than that 44.1 is what CDs used.
    /// </summary>
    [Range(44100, 48000)]
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>
    /// How long to keep retrying the source before giving up on it.
    ///
    /// The source blinking is normal: a broadcaster reconnecting drops the RTSP read while the
    /// session sits in RECONNECTING. Restarting internally keeps that from being reported as a
    /// destination failure, which would otherwise burn the destination's retry budget for something
    /// that has nothing to do with the destination.
    /// </summary>
    [Range(0, 3600)]
    public int SourceRecoveryWindowSeconds { get; set; } = 150;

    /// <summary>Delay between internal source-recovery restarts.</summary>
    [Range(1, 60)]
    public int SourceRestartDelaySeconds { get; set; } = 3;

    /// <summary>Maximum concurrent relays, as a resource guard.</summary>
    [Range(1, 500)]
    public int MaxConcurrentRelays { get; set; } = 50;

    public TimeSpan SourceInspectionTimeout => TimeSpan.FromSeconds(SourceInspectionTimeoutSeconds);

    public TimeSpan SourceRecoveryWindow => TimeSpan.FromSeconds(SourceRecoveryWindowSeconds);

    public TimeSpan SourceRestartDelay => TimeSpan.FromSeconds(SourceRestartDelaySeconds);
}

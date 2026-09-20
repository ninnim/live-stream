using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace LiveStream.Relay;

/// <summary>
/// Supervises one encoder process: pulls a session from the media gateway and republishes it to one
/// external RTMP endpoint.
///
/// The interesting behaviour is the distinction between a source problem and a destination problem.
/// A broadcaster reconnecting drops the source read, and reporting that upward as a destination
/// failure would spend the destination's retry budget on something the destination did not do. So
/// source loss is recovered here, inside a bounded window, and only destination failures are
/// reported to the control plane.
/// </summary>
public sealed class RelayWorker : IAsyncDisposable
{
    private readonly StartRelayRequest _request;
    private readonly RelayServiceOptions _options;
    private readonly ILogger _logger;

    /// <summary>Chosen once for the machine, not per relay. See <see cref="EncoderProbe"/>.</summary>
    private readonly VideoEncoder _encoder;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// The encoder's opening lines, which carry the stream declarations.
    ///
    /// Kept separately from the tail below, and that separation is load-bearing. A single bounded
    /// buffer filled up with the banner and then stopped, so the error that ended the process — the
    /// thing failures are classified on — never made it in, and every failure classified as
    /// unrecognised.
    /// </summary>
    private readonly List<string> _encoderBanner = [];

    /// <summary>
    /// The encoder's most recent lines. Whatever killed it is at the end, so this is what a failure
    /// is classified on.
    /// </summary>
    private readonly Queue<string> _encoderTail = new();

    private const int BannerLines = 40;
    private const int TailLines = 60;

    private Task? _supervision;
    private Process? _process;

    private RelayPhase _phase = RelayPhase.Starting;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _connectedAt;
    private long _bytesSent;
    private long _bytesSentBaseline;
    private int _restartCount;
    private string? _lastErrorCode;
    private string? _lastErrorMessage;
    private DateTimeOffset? _lastErrorAt;

    private readonly ISourceInspector _sourceInspector;

    public RelayWorker(
        StartRelayRequest request,
        RelayServiceOptions options,
        VideoEncoder encoder,
        ISourceInspector sourceInspector,
        ILogger logger)
    {
        _request = request;
        _options = options;
        _encoder = encoder;
        _sourceInspector = sourceInspector;
        _logger = logger;
    }

    public Guid DestinationId => _request.DestinationId;

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        _supervision = Task.Run(() => SuperviseAsync(_stopping.Token));
    }

    public RelayStateResponse Snapshot()
    {
        lock (_gate)
        {
            return new RelayStateResponse
            {
                DestinationId = _request.DestinationId,
                Phase = _phase.ToString(),
                StartedAt = _startedAt,
                ConnectedAt = _connectedAt,
                BytesSent = _bytesSent,
                RestartCount = _restartCount,
                LastErrorCode = _lastErrorCode,
                LastErrorMessage = _lastErrorMessage,
                LastErrorAt = _lastErrorAt,
            };
        }
    }

    // -----------------------------------------------------------------------------------------
    // Supervision
    // -----------------------------------------------------------------------------------------

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var sourceFailingSince = (DateTimeOffset?)null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await RunEncoderAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (outcome.Kind == FailureKind.SourceUnavailable)
            {
                sourceFailingSince ??= DateTimeOffset.UtcNow;
                var failingFor = DateTimeOffset.UtcNow - sourceFailingSince.Value;

                if (failingFor <= _options.SourceRecoveryWindow)
                {
                    lock (_gate)
                    {
                        _restartCount++;
                        _phase = RelayPhase.Starting;
                        _connectedAt = null;

                        // The next process starts its byte counter at zero, so the total already
                        // pushed is carried forward as a baseline. Without this, throughput would
                        // appear to reset every time the broadcaster reconnected.
                        _bytesSentBaseline = _bytesSent;
                    }

                    _logger.LogInformation(
                        "Relay {DestinationId} source unavailable, restarting ({RestartCount}); waited {Seconds}s of {Window}s",
                        _request.DestinationId, _restartCount, (int)failingFor.TotalSeconds,
                        _options.SourceRecoveryWindowSeconds);

                    await Task.Delay(_options.SourceRestartDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // The source never came back inside the window. That is a real end state, and the
                // control plane should hear about it.
                Fail(RelayErrorCodes.DestinationUnavailable,
                    "The broadcast source did not come back.");
                break;
            }

            if (outcome.Kind == FailureKind.None)
            {
                // The encoder exited cleanly, which means the source ended normally.
                lock (_gate)
                {
                    _phase = RelayPhase.Stopped;
                }

                break;
            }

            Fail(outcome.ErrorCode!, outcome.Message!);
            break;
        }
    }

    private async Task<EncoderOutcome> RunEncoderAsync(CancellationToken cancellationToken)
    {
        var sourceUrl = BuildSourceUrl();

        // Established per run, not once per relay. A broadcaster who reconnects having plugged in a
        // microphone changes the answer, and a relay recovering from that restart must not keep
        // synthesising silence over the sound that has just appeared.
        SourceTracks tracks;
        try
        {
            tracks = _options.SynthesizeMissingTracks
                ? await _sourceInspector.InspectAsync(sourceUrl, cancellationToken).ConfigureAwait(false)
                : SourceTracks.Unknown;
        }
        catch (OperationCanceledException)
        {
            // Stopped while inspecting. Starting an encoder now would only be killed a moment later.
            return EncoderOutcome.Stopped();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(sourceUrl, tracks))
        {
            // ArgumentList quotes each value for the platform, so a stream key containing a space
            // or a quote cannot break out of the command line.
            startInfo.ArgumentList.Add(argument);
        }

        if (tracks.NeedsSilentAudio || tracks.NeedsBlankVideo)
        {
            // Worth a line every run: it is the difference between a stream a platform shows and
            // one it accepts and hides, and it is otherwise invisible from the outside.
            _logger.LogInformation(
                "Relay {DestinationId} source carries {Tracks}; synthesising the missing {Missing}",
                _request.DestinationId, tracks.Describe(),
                tracks.NeedsSilentAudio ? "audio as silence" : "video as a blank picture");
        }

        lock (_gate)
        {
            _encoderBanner.Clear();
            _encoderTail.Clear();
        }
        var sawProgress = false;

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                if (TryReadProgress(args.Data))
                {
                    sawProgress = true;
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                // Both ends, both bounded: the opening lines describe the streams, the closing ones
                // say what went wrong, and a failing encoder can produce a great deal in between.
                lock (_gate)
                {
                    if (_encoderBanner.Count < BannerLines)
                    {
                        _encoderBanner.Add(args.Data);
                    }

                    _encoderTail.Enqueue(args.Data);
                    while (_encoderTail.Count > TailLines)
                    {
                        _encoderTail.Dequeue();
                    }
                }
            };

            if (!process.Start())
            {
                return EncoderOutcome.Failed(RelayErrorCodes.RelayUnavailable, "The encoder did not start.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the encoder for {DestinationId} failed", _request.DestinationId);
            return EncoderOutcome.Failed(RelayErrorCodes.RelayUnavailable, "The encoder could not be started.");
        }

        lock (_gate)
        {
            _process = process;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            return EncoderOutcome.Stopped();
        }
        finally
        {
            lock (_gate)
            {
                _process = null;
            }
        }

        var exitCode = process.ExitCode;
        string errorText;
        lock (_gate)
        {
            // Both ends: the banner catches a failure that happened before the encoder produced
            // anything else, the tail catches one that ended a run already in progress.
            errorText = string.Join("\n", _encoderBanner.Concat(_encoderTail));
        }
        process.Dispose();

        if (exitCode == 0)
        {
            return EncoderOutcome.Clean();
        }

        return Classify(errorText, sawProgress);
    }

    /// <summary>
    /// Decides what a non-zero encoder exit actually means.
    ///
    /// The heuristics are stated explicitly rather than hidden: the encoder reports failures as
    /// prose, and there is no structured alternative. Anything unrecognised is treated as a
    /// retryable destination problem, because an unnecessary retry costs seconds while wrongly
    /// giving up ends distribution for the rest of the broadcast.
    /// </summary>
    private EncoderOutcome Classify(string stderrText, bool sawProgress)
    {
        var text = stderrText.ToLowerInvariant();

        // Checked first: these are unambiguous refusals from the platform, and retrying them only
        // risks the account being rate-limited.
        string[] rejectionMarkers =
        [
            "401 unauthorized", "403 forbidden", "authentication failed",
            "rtmp_connect", "invalid stream key", "not authorized", "access denied",
        ];

        if (rejectionMarkers.Any(text.Contains) && !IsSourceProblem(text, sawProgress))
        {
            return EncoderOutcome.Failed(RelayErrorCodes.DestinationRejected,
                "The platform rejected the stream. Check the stream key.");
        }

        if (IsSourceProblem(text, sawProgress))
        {
            return EncoderOutcome.SourceUnavailable();
        }

        return EncoderOutcome.Failed(RelayErrorCodes.DestinationUnavailable,
            "The connection to the platform failed.");
    }

    /// <summary>
    /// True when the failure is on the input side. An encoder that never produced a single progress
    /// record never got as far as writing to the platform, so the problem is upstream of it.
    /// </summary>
    /// <summary>Words that mark a line as reporting a failure rather than describing the run.</summary>
    private static readonly string[] FailurePhrases =
    [
        "error", "failed", "refused", "timed out", "timeout", "not found", "unauthorized",
        "forbidden", "denied", "invalid", "broken pipe", "end of file", "immediate exit",
    ];

    /// <summary>
    /// Whether the encoder died because of its source rather than its destination.
    ///
    /// Decided line by line, and only on lines that report a failure. A whole-text search for
    /// <c>rtsp://</c> looked equivalent and was not: the encoder names its input URL in the banner
    /// of every successful run, so once that banner was captured, every destination outage
    /// classified as a source outage and the relay restarted instead of reporting the platform had
    /// gone. Both ends of a relay speak URLs; only the failing line says which one broke.
    /// </summary>
    private bool IsSourceProblem(string lowercaseStderr, bool sawProgress)
    {
        var failing = lowercaseStderr
            .Split('\n')
            .Where(line => FailurePhrases.Any(line.Contains))
            .ToArray();

        // We asked it to stop, which is not a failure of either end.
        if (failing.Any(line => line.Contains("immediate exit")))
        {
            return true;
        }

        if (failing.Any(line =>
                line.Contains("rtsp") || line.Contains("describe") || line.Contains("codec parameters")))
        {
            return true;
        }

        if (failing.Any(line => line.Contains("rtmp")))
        {
            return false;
        }

        // Nothing named either end. No bytes ever reached the platform means the input never opened.
        return !sawProgress;
    }

    /// <summary>
    /// Reads one <c>-progress</c> line. Progress only appears once the encoder is writing to the
    /// output, which is what makes it a reliable signal that the platform accepted the connection.
    /// </summary>
    private bool TryReadProgress(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var key = line.AsSpan(0, separator);
        var value = line.AsSpan(separator + 1);

        if (!key.SequenceEqual("total_size"))
        {
            return false;
        }

        if (!long.TryParse(value, out var totalSize))
        {
            return false;
        }

        lock (_gate)
        {
            _bytesSent = _bytesSentBaseline + totalSize;

            if (_phase == RelayPhase.Starting && totalSize > 0)
            {
                _phase = RelayPhase.Connected;
                _connectedAt = DateTimeOffset.UtcNow;

                // The encoder banner, once, at the moment it is known to be working. It carries
                // the input resolution and codec, which is what a platform silently refusing a
                // stream is usually objecting to.
                _logger.LogInformation(
                    "Relay {DestinationId} connected to {Provider}; encoder: {Encoder}",
                    _request.DestinationId, _request.Provider, DescribeStreams());
            }
        }

        return true;
    }

    /// <summary>
    /// Black, and deliberately not 1080p.
    ///
    /// A synthesised picture exists to satisfy a platform that will not show an audio-only stream,
    /// not to be looked at. 720p is accepted everywhere and costs a fraction of a percent of the
    /// bitrate budget once x264 sees that nothing in it ever changes.
    /// </summary>
    private const string BlankVideoSize = "1280x720";

    /// <summary>
    /// Frames per second the blank picture is generated at, before the output rate normalises it.
    ///
    /// Deliberately far below the output rate. A black frame has no motion to preserve, so
    /// generating sixty of them a second would spend real CPU — 1280x720 raw is 1.3 MB a frame —
    /// producing images that are byte-identical. The output <c>-r</c> duplicates them up to the
    /// rate the platform is sent, and duplicated identical frames cost x264 almost nothing.
    /// </summary>
    private const int BlankVideoFrameRate = 10;

    internal IEnumerable<string> BuildArguments(string sourceUrl, SourceTracks tracks)
    {
        // Never both: see SourceTracks. One of the two kinds has to be real for the other to be
        // worth generating.
        var synthesizeVideo = tracks.NeedsBlankVideo;
        var synthesizeAudio = tracks.NeedsSilentAudio;

        // Synthesising the picture means it cannot be copied: there is no source stream to copy.
        var copyVideo = _options.VideoCodec == "copy" && !synthesizeVideo;

        yield return "-hide_banner";
        yield return "-nostdin";
        yield return "-loglevel";
        // `info` rather than `warning`, and stats suppressed: the banner FFmpeg prints at
        // info level is the only place the actual input resolution, codec and frame rate
        // appear, and diagnosing "the platform accepted it and showed nothing" without them
        // is guesswork. Structured progress already arrives on stdout, so the per-second
        // stats line would only crowd the banner out of a bounded buffer.
        yield return "info";
        yield return "-nostats";

        // TCP rather than UDP for the internal read: this hop is inside the container network where
        // there is no reason to tolerate packet loss, and RTSP-over-TCP avoids reordering entirely.
        yield return "-rtsp_transport";
        yield return "tcp";

        yield return "-i";
        yield return sourceUrl;

        // The synthesised kind is a second input, so the real source stays input 0 and the maps
        // below read the same way whichever kind is missing.
        if (synthesizeAudio)
        {
            yield return "-f";
            yield return "lavfi";
            yield return "-i";
            yield return $"anullsrc=channel_layout=stereo:sample_rate={_options.AudioSampleRate.ToString(CultureInfo.InvariantCulture)}";
        }
        else if (synthesizeVideo)
        {
            yield return "-f";
            yield return "lavfi";
            yield return "-i";
            yield return
                $"color=c=black:s={BlankVideoSize}:r={BlankVideoFrameRate.ToString(CultureInfo.InvariantCulture)}";
        }

        if (synthesizeAudio || synthesizeVideo)
        {
            // Explicit once there are two inputs: the default picks the "best" stream of each kind
            // across all inputs, which is not a rule to leave a live broadcast resting on.
            yield return "-map";
            yield return synthesizeVideo ? "1:v" : "0:v";
            yield return "-map";
            yield return synthesizeAudio ? "1:a" : "0:a";

            // The generated input never ends. Without this the encoder would keep running after
            // the broadcaster stopped, holding the destination open and pushing a black frame or
            // silence at the platform indefinitely.
            yield return "-shortest";
        }

        if (copyVideo)
        {
            yield return "-c:v";
            yield return "copy";
        }
        else
        {
            // Fit inside the platform's largest accepted frame, never upscale, and keep both
            // dimensions even.
            //
            // A shared screen arrives at whatever the display is: 1440p and 4K monitors are
            // ordinary, and a shared *window* can be any odd size at all. Platforms top out at
            // 1080p and accept a larger stream without ever showing it, and no H.264 encoder can
            // encode an odd width to 4:2:0 at all. Both failures look identical from here —
            // bytes leaving, nothing appearing — so neither is left to chance.
            yield return "-vf";
            yield return
                $"scale=w='min({_options.MaxVideoWidth},iw)':h='min({_options.MaxVideoHeight},ih)'"
                + ":force_original_aspect_ratio=decrease:force_divisible_by=2";

            // A constant output rate, because a WebRTC source does not have one. A shared
            // screen only produces frames when something changes — a static page can fall
            // under one per second — and an RTMP stream that arrives at that rate is one
            // most platforms will accept and then never show.
            yield return "-r";
            yield return _options.VideoFrameRate.ToString(CultureInfo.InvariantCulture);

            // Codec, preset and pixel format all come from the encoder actually selected for this
            // machine: an x264 preset means nothing to NVENC, and Quick Sync wants NV12.
            foreach (var argument in _encoder.Arguments(_options.VideoBitrateKbps, _options.VideoPreset))
            {
                yield return argument;
            }

            // A keyframe every two seconds, expressed in *time* rather than in frames.
            //
            // `-g` counts frames, so on a source delivering two frames a second it would put
            // sixty seconds between keyframes. Platforms segment on keyframes and cannot
            // start playback without one, which is exactly the failure where the relay
            // reports a healthy connection, bytes arrive, and the platform shows nothing.
            //
            // `-g` is kept as well: it bounds the interval when the source is fast, and
            // gives x264 a sensible GOP to plan against.
            yield return "-force_key_frames";
            yield return "expr:gte(t,n_forced*2)";

            yield return "-g";
            yield return (_options.VideoFrameRate * 2).ToString(CultureInfo.InvariantCulture);
        }

        // Always transcoded: browsers publish Opus over WebRTC and RTMP carries AAC.
        yield return "-c:a";
        yield return "aac";
        yield return "-b:a";
        yield return $"{_options.AudioBitrateKbps}k";
        yield return "-ar";
        yield return _options.AudioSampleRate.ToString(CultureInfo.InvariantCulture);
        yield return "-ac";
        yield return "2";

        yield return "-f";
        yield return "flv";

        yield return "-progress";
        yield return "pipe:1";
        yield return "-stats_period";
        yield return "1";

        yield return BuildTargetUrl();
    }

    /// <summary>
    /// The read credential travels as the RTSP password, matching how the studio presents its
    /// publish credential over WHIP. The gateway forwards it to the control plane auth hook.
    /// </summary>
    private string BuildSourceUrl()
    {
        var baseUrl = _options.SourceRtspBaseUrl.TrimEnd('/');
        var authority = baseUrl.Replace("rtsp://", string.Empty, StringComparison.Ordinal);

        return $"rtsp://relay:{Uri.EscapeDataString(_request.SourceCredential)}@{authority}/{_request.MediaPathName}";
    }

    /// <summary>
    /// One line describing what the encoder is reading and writing, taken from its banner.
    ///
    /// Only the stream declarations are kept. The banner also carries build flags and library
    /// versions, which are noise here, and the target URL, which carries the stream key and must
    /// never reach a log.
    /// </summary>
    private string DescribeStreams()
    {
        string[] banner;
        lock (_gate)
        {
            banner = [.. _encoderBanner];
        }

        var streams = banner
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Stream #", StringComparison.Ordinal))
            .Take(4)
            .ToArray();

        return streams.Length == 0 ? "not reported" : string.Join(" | ", streams);
    }

    private string BuildTargetUrl() =>
        $"{_request.TargetUrl.TrimEnd('/')}/{_request.TargetStreamKey}";

    private void Fail(string errorCode, string message)
    {
        lock (_gate)
        {
            _phase = RelayPhase.Failed;
            _lastErrorCode = errorCode;
            _lastErrorMessage = message;
            _lastErrorAt = DateTimeOffset.UtcNow;
        }

        _logger.LogWarning("Relay {DestinationId} failed: {ErrorCode} {Message}",
            _request.DestinationId, errorCode, message);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process exited between the check and the kill; nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        Process? process;
        lock (_gate)
        {
            process = _process;
            _phase = RelayPhase.Stopped;
        }

        if (process is not null)
        {
            KillQuietly(process);
        }

        if (_supervision is not null)
        {
            try
            {
                await _supervision.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // A supervision loop that will not settle must not block the shutdown of the rest.
            }
        }

        _stopping.Dispose();
    }

    private enum FailureKind
    {
        None,
        SourceUnavailable,
        Destination,
    }

    private sealed record EncoderOutcome(FailureKind Kind, string? ErrorCode, string? Message)
    {
        public static EncoderOutcome Clean() => new(FailureKind.None, null, null);

        public static EncoderOutcome Stopped() => new(FailureKind.None, null, null);

        public static EncoderOutcome SourceUnavailable() => new(FailureKind.SourceUnavailable, null, null);

        public static EncoderOutcome Failed(string errorCode, string message) =>
            new(FailureKind.Destination, errorCode, message);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace LiveStream.Relay;

/// <summary>Whether a kind of media is on the source, or whether that could not be established.</summary>
public enum SourceTrackState
{
    /// <summary>The source could not be inspected. Never treated as evidence of anything.</summary>
    Unknown,
    Present,
    Absent,
}

/// <summary>
/// What a broadcast source is actually carrying.
///
/// This exists because platforms are unforgiving about a stream that is missing a kind, and silent
/// about it. Facebook Live requires an audio track: a video-only stream is accepted, counted, and
/// never shown to anybody. So the relay has to know what it is reading before it decides what to
/// send, and "I could not tell" has to be a distinct answer from "there is none" — synthesising
/// audio over a source that has some would replace a broadcaster's voice with silence, which is a
/// far worse failure than the one being fixed.
/// </summary>
public sealed record SourceTracks(SourceTrackState Video, SourceTrackState Audio)
{
    public static SourceTracks Unknown { get; } =
        new(SourceTrackState.Unknown, SourceTrackState.Unknown);

    public static SourceTracks Of(bool hasVideo, bool hasAudio) =>
        new(
            hasVideo ? SourceTrackState.Present : SourceTrackState.Absent,
            hasAudio ? SourceTrackState.Present : SourceTrackState.Absent);

    /// <summary>
    /// A silent track has to be generated: there is a picture and no sound.
    ///
    /// Both conditions, deliberately. A source reporting *neither* kind is not a broadcast, and
    /// generating one half of one would produce an encoder mapping two output streams to a single
    /// generated input — which fails at startup rather than reporting the real problem, that there
    /// is nothing to relay.
    /// </summary>
    public bool NeedsSilentAudio =>
        Audio == SourceTrackState.Absent && Video == SourceTrackState.Present;

    /// <summary>A blank picture has to be generated: there is sound and no picture.</summary>
    public bool NeedsBlankVideo =>
        Video == SourceTrackState.Absent && Audio == SourceTrackState.Present;

    /// <summary>Safe to log: describes kinds, never the URL the credential travels in.</summary>
    public string Describe() => $"video={Video}, audio={Audio}";
}

/// <summary>Reads a source's track list. Injectable so argument building can be tested.</summary>
public interface ISourceInspector
{
    Task<SourceTracks> InspectAsync(string sourceUrl, CancellationToken cancellationToken);
}

/// <summary>
/// Asks ffprobe what the source carries.
///
/// RTSP answers a DESCRIBE with an SDP that lists the tracks, so this returns almost immediately on
/// a live source and does not have to read any media.
///
/// <para>
/// Nothing ffprobe writes is ever logged. The source URL carries the gateway read credential as its
/// RTSP password, and ffprobe echoes the URL it was given in most of its error messages — so
/// forwarding its output to a log would put a credential there. Only the outcome is reported.
/// </para>
/// </summary>
public sealed class FfprobeSourceInspector(
    IOptions<RelayServiceOptions> options,
    ILogger<FfprobeSourceInspector> logger) : ISourceInspector
{
    private readonly RelayServiceOptions _options = options.Value;

    public async Task<SourceTracks> InspectAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected so it can be closed. ffprobe has no `-nostdin` — that is an ffmpeg option,
            // and passing it here makes ffprobe swallow the next argument as its value and exit,
            // which looked exactly like a source that could not be inspected.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in (string[])
                 [
                     "-hide_banner", "-loglevel", "error",
                     "-rtsp_transport", "tcp",
                     "-show_entries", "stream=codec_type",
                     "-of", "csv=p=0",
                     "-i", sourceUrl,
                 ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return SourceTracks.Unknown;
            }

            process.StandardInput.Close();

            // Read before waiting: ffprobe's output is small, but a process whose pipe fills while
            // nobody is draining it deadlocks, and that would hang every relay start.
            var readOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var readError = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SourceInspectionTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                return SourceTracks.Unknown;
            }

            // Awaited so the pipes are closed before the process object goes away. Discarded
            // deliberately: see the note above about what ffprobe puts in its error text.
            var output = await readOutput;
            _ = await readError;

            if (process.ExitCode != 0)
            {
                // Usually the source not publishing yet, which the encoder is about to discover for
                // itself and recover from. Logged at information rather than debug all the same:
                // when it is *not* that, the consequence is a stream a platform accepts and never
                // shows, and this line is the only place that would be visible.
                logger.LogInformation(
                    "Source inspection exited {ExitCode}; sending the stream exactly as it arrives",
                    process.ExitCode);
                return SourceTracks.Unknown;
            }

            return Parse(output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing binary or a refused exec. Reported once, and the relay carries on doing
            // exactly what it did before this inspection existed.
            logger.LogWarning(ex, "Could not inspect the broadcast source; proceeding without it");
            return SourceTracks.Unknown;
        }
    }

    /// <summary>
    /// Reads one <c>codec_type</c> per line.
    ///
    /// An empty answer is <see cref="SourceTracks.Unknown"/> rather than "no tracks at all": a
    /// successful probe always names at least one, so nothing means the probe did not really
    /// succeed, and treating that as an absent audio track would synthesise silence over a
    /// broadcast that has sound.
    /// </summary>
    internal static SourceTracks Parse(string ffprobeOutput)
    {
        var kinds = ffprobeOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (kinds.Length == 0)
        {
            return SourceTracks.Unknown;
        }

        return SourceTracks.Of(
            hasVideo: kinds.Contains("video", StringComparer.OrdinalIgnoreCase),
            hasAudio: kinds.Contains("audio", StringComparer.OrdinalIgnoreCase));
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
            // Exited between the check and the kill.
        }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Relay;

/// <summary>Runs a short trial encode. Injectable so selection can be tested without a GPU.</summary>
public interface IEncoderTrial
{
    Task<bool> CanEncodeAsync(VideoEncoder encoder, CancellationToken cancellationToken);
}

/// <summary>
/// Decides which encoder this machine can actually use, once, at startup.
///
/// The decision has to be a trial encode rather than a capability listing, and that is the whole
/// point of this class. `ffmpeg -encoders` lists every encoder the binary was *built* with: the
/// stock Debian build reports NVENC, Quick Sync and VAAPI on a machine with no GPU at all, and
/// choosing one on that evidence fails at the first frame with `Cannot load libcuda.so.1` — after
/// the broadcast has started, on the operator's stream rather than here.
///
/// So each candidate encodes a fraction of a second of blank video to nothing. What survives that
/// is what the hardware really offers.
/// </summary>
public sealed class EncoderProbe(
    IEncoderTrial trial,
    IOptions<RelayServiceOptions> options,
    ILogger<EncoderProbe> logger)
{
    private readonly RelayServiceOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VideoEncoder? _selected;

    /// <summary>
    /// What the probe settled on. Software until it has run, which is the safe assumption:
    /// x264 works everywhere, and a relay that started before the probe finished should not
    /// gamble on hardware.
    /// </summary>
    public VideoEncoder Selected => _selected ?? VideoEncoder.Software;

    /// <summary>
    /// The encoder to use. Probed on first call and remembered: hardware does not appear or vanish
    /// while the service runs, and probing per relay would add a subprocess to every broadcast.
    /// </summary>
    public async Task<VideoEncoder> SelectAsync(CancellationToken cancellationToken)
    {
        if (_selected is not null)
        {
            return _selected;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            _selected ??= await ProbeAsync(cancellationToken);
            return _selected;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VideoEncoder> ProbeAsync(CancellationToken cancellationToken)
    {
        // An operator naming an encoder is trusted over the probe: they may know something it
        // cannot, such as a driver that only initialises under real load. It is still tried, so a
        // wrong name is reported at startup rather than on the first broadcast.
        if (!string.Equals(_options.VideoCodec, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var named = VideoEncoder.ByName(_options.VideoCodec);

            if (named is null)
            {
                logger.LogWarning(
                    "Configured encoder {Codec} is not one this relay knows; using {Fallback}",
                    _options.VideoCodec, VideoEncoder.Software.DisplayName);
                return VideoEncoder.Software;
            }

            if (!await trial.CanEncodeAsync(named, cancellationToken))
            {
                logger.LogWarning(
                    "Configured encoder {Codec} could not encode a test frame; using {Fallback}",
                    named.DisplayName, VideoEncoder.Software.DisplayName);
                return VideoEncoder.Software;
            }

            logger.LogInformation("Using configured encoder {Encoder}", named.DisplayName);
            return named;
        }

        foreach (var candidate in VideoEncoder.Candidates)
        {
            if (await trial.CanEncodeAsync(candidate, cancellationToken))
            {
                logger.LogInformation(
                    "Selected video encoder {Encoder} (hardware={IsHardware})",
                    candidate.DisplayName, candidate.IsHardware);

                return candidate;
            }

            logger.LogDebug("Encoder {Encoder} is unavailable on this machine", candidate.DisplayName);
        }

        // libx264 is the last candidate and is built into every ffmpeg, so reaching here means the
        // encoder binary itself is broken. Say so, and let the relay fail visibly on first use.
        logger.LogError("No usable video encoder was found, not even libx264. Check the ffmpeg install.");
        return VideoEncoder.Software;
    }
}

/// <summary>Encodes a fraction of a second of blank video to nothing, and reports whether it worked.</summary>
public sealed class FfmpegEncoderTrial(IOptions<RelayServiceOptions> options) : IEncoderTrial
{
    /// <summary>
    /// Long enough for a driver to initialise, short enough that four failing candidates cannot
    /// delay startup noticeably.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly RelayServiceOptions _options = options.Value;

    public async Task<bool> CanEncodeAsync(VideoEncoder encoder, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 720p rather than a token 16x16: some hardware encoders accept a tiny frame and then fail
        // on a real one, which would make the probe worse than useless.
        foreach (var argument in (string[])
                 [
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-f", "lavfi", "-i", "nullsrc=s=1280x720:r=30:d=0.1",
                     .. encoder.Arguments(bitrateKbps: 1000, softwarePreset: "veryfast"),
                     "-f", "null", "-",
                 ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // A hung probe is a failed probe: an encoder that cannot initialise in ten seconds
                // is not one to hand a live broadcast to.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // A missing binary or a refused exec is simply an encoder that cannot be used.
            return false;
        }
    }
}

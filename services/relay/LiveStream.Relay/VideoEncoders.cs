using System.Globalization;

namespace LiveStream.Relay;

/// <summary>
/// A hardware or software H.264 encoder the relay can use, and the arguments it needs.
///
/// Encoders are not interchangeable on the command line. `-preset veryfast` is an x264 preset and
/// means nothing to NVENC, which numbers its own p1–p7; QSV wants NV12 rather than YUV420P. Keeping
/// each one's arguments beside its name is what stops a wrong flag being discovered live.
/// </summary>
public sealed record VideoEncoder(string Name, string DisplayName, bool IsHardware)
{
    /// <summary>
    /// Preference order. Hardware first: it frees the CPU the rest of the relay competes for, and
    /// at these bitrates the quality difference against x264 veryfast is not the deciding factor.
    ///
    /// VAAPI is deliberately absent. It encodes from hardware frames, so it needs its own upload
    /// filter chain rather than the scale filter everything else shares, and half-supporting it
    /// would mean an encoder that probes clean and then fails on the first real frame.
    /// </summary>
    public static readonly IReadOnlyList<VideoEncoder> Candidates =
    [
        new("h264_nvenc", "NVIDIA NVENC", IsHardware: true),
        new("h264_qsv", "Intel Quick Sync", IsHardware: true),
        new("libx264", "libx264 (CPU)", IsHardware: false),
    ];

    public static VideoEncoder Software => Candidates[^1];

    public static VideoEncoder? ByName(string name) =>
        Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Encoding arguments for one bitrate, sized in kbps.
    ///
    /// Every encoder gets a hard ceiling and a buffer, not just an average: a platform's ingest
    /// rejects a stream that overshoots, and an unconstrained encoder overshoots on a scene change.
    /// </summary>
    public IEnumerable<string> Arguments(int bitrateKbps, string softwarePreset)
    {
        var bitrate = $"{bitrateKbps.ToString(CultureInfo.InvariantCulture)}k";
        var buffer = $"{(bitrateKbps * 2).ToString(CultureInfo.InvariantCulture)}k";

        yield return "-c:v";
        yield return Name;

        switch (Name)
        {
            case "h264_nvenc":
                // p4 is NVENC's balanced point and `ll` is its low-latency tuning, which is what a
                // live stream wants: no lookahead means no added delay.
                yield return "-preset";
                yield return "p4";
                yield return "-tune";
                yield return "ll";
                yield return "-rc";
                yield return "cbr";
                yield return "-pix_fmt";
                yield return "yuv420p";
                break;

            case "h264_qsv":
                yield return "-preset";
                yield return "veryfast";

                // Quick Sync encodes from NV12; handing it YUV420P costs a conversion per frame.
                yield return "-pix_fmt";
                yield return "nv12";
                break;

            default:
                yield return "-preset";
                yield return softwarePreset;

                // Every platform expects 4:2:0 8-bit; a browser can hand over other formats.
                yield return "-pix_fmt";
                yield return "yuv420p";
                break;
        }

        yield return "-b:v";
        yield return bitrate;
        yield return "-maxrate";
        yield return bitrate;
        yield return "-bufsize";
        yield return buffer;
    }
}

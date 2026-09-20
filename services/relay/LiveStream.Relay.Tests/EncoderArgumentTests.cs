using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveStream.Relay.Tests;

/// <summary>
/// The encoder command line, which is the whole behaviour of this service.
///
/// These are worth testing directly because every mistake in them fails the same way: the relay
/// reports a healthy connection, bytes leave for the platform, and nothing is ever shown. There is
/// no error to read and no log to look at.
/// </summary>
public sealed class EncoderArgumentTests
{
    private const string SourceUrl = "rtsp://relay:secret@mediamtx:8554/ls_abc";

    private static RelayWorker Worker(RelayServiceOptions? options = null) =>
        new(
            new StartRelayRequest
            {
                DestinationId = Guid.NewGuid(),
                MediaPathName = "ls_abc",
                SourceCredential = "secret",
                TargetUrl = "rtmp://a.rtmp.youtube.com/live2",
                TargetStreamKey = "key",
            },
            options ?? new RelayServiceOptions { SharedSecret = new string('s', 16) },
            VideoEncoder.Software,
            new StubInspector(SourceTracks.Unknown),
            NullLogger.Instance);

    private static string[] Arguments(SourceTracks tracks, RelayServiceOptions? options = null) =>
        [.. Worker(options).BuildArguments(SourceUrl, tracks)];

    /// <summary>Reads the value that follows a flag, so order-independent assertions stay readable.</summary>
    private static IEnumerable<string> ValuesOf(string[] arguments, string flag)
    {
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == flag)
            {
                yield return arguments[i + 1];
            }
        }
    }

    [Fact]
    public void A_source_with_both_kinds_is_sent_exactly_as_it_arrives()
    {
        var arguments = Arguments(SourceTracks.Of(hasVideo: true, hasAudio: true));

        Assert.Single(ValuesOf(arguments, "-i"));
        Assert.DoesNotContain("lavfi", arguments);
        Assert.DoesNotContain("-map", arguments);

        // No generated input means nothing to outlast the source, so nothing to cut short.
        Assert.DoesNotContain("-shortest", arguments);
    }

    [Fact]
    public void A_video_only_source_gains_a_silent_audio_track()
    {
        // The reason this whole path exists. Facebook Live accepts a video-only stream, counts it
        // as live, and never shows it to anybody.
        var arguments = Arguments(SourceTracks.Of(hasVideo: true, hasAudio: false));

        var inputs = ValuesOf(arguments, "-i").ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.Equal(SourceUrl, inputs[0]);
        Assert.StartsWith("anullsrc=", inputs[1], StringComparison.Ordinal);

        // The picture comes from the broadcast, the sound from the generator — never the reverse.
        Assert.Equal(["0:v", "1:a"], ValuesOf(arguments, "-map"));
        Assert.Contains("-shortest", arguments);
    }

    [Fact]
    public void An_audio_only_source_gains_a_blank_picture()
    {
        var arguments = Arguments(SourceTracks.Of(hasVideo: false, hasAudio: true));

        var inputs = ValuesOf(arguments, "-i").ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.StartsWith("color=c=black:", inputs[1], StringComparison.Ordinal);

        Assert.Equal(["1:v", "0:a"], ValuesOf(arguments, "-map"));
        Assert.Contains("-shortest", arguments);
    }

    [Fact]
    public void A_blank_picture_is_generated_slowly_and_normalised_on_the_way_out()
    {
        // Black frames are byte-identical, so generating them at the output rate would spend CPU on
        // 1.3 MB of raw image per frame to no effect. The output rate is what the platform sees.
        var arguments = Arguments(SourceTracks.Of(hasVideo: false, hasAudio: true));

        Assert.Contains("color=c=black:s=1280x720:r=10", arguments);
        Assert.Contains("60", ValuesOf(arguments, "-r"));
    }

    [Fact]
    public void Silence_is_generated_at_the_rate_the_platform_is_being_sent()
    {
        // A mismatch here would make the encoder resample digital silence, which works and is
        // simply waste — but it also means the two settings can drift apart unnoticed.
        var options = new RelayServiceOptions { SharedSecret = new string('s', 16), AudioSampleRate = 48000 };
        var arguments = Arguments(SourceTracks.Of(hasVideo: true, hasAudio: false), options);

        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", arguments);
        Assert.Contains("48000", ValuesOf(arguments, "-ar"));
    }

    [Fact]
    public void An_unknown_source_is_left_alone()
    {
        // The dangerous direction. Synthesising audio over a source that has some would replace a
        // broadcaster's voice with silence, which is far worse than the failure being fixed — so a
        // probe that could not answer must change nothing.
        var arguments = Arguments(SourceTracks.Unknown);

        Assert.Single(ValuesOf(arguments, "-i"));
        Assert.DoesNotContain("-map", arguments);
        Assert.DoesNotContain("-shortest", arguments);
    }

    [Fact]
    public void Copying_the_video_stays_available_when_the_source_has_one()
    {
        var options = new RelayServiceOptions { SharedSecret = new string('s', 16), VideoCodec = "copy" };

        var arguments = Arguments(SourceTracks.Of(hasVideo: true, hasAudio: false), options);

        Assert.Contains("copy", ValuesOf(arguments, "-c:v"));

        // Still gains the silent track, and still only the audio map points at the generator.
        Assert.Equal(["0:v", "1:a"], ValuesOf(arguments, "-map"));
    }

    [Fact]
    public void A_synthesised_picture_is_encoded_even_when_the_relay_is_set_to_copy()
    {
        // There is no source video stream to copy. Honouring `copy` here would produce an encoder
        // that fails at startup on a configuration that works for every other broadcast.
        var options = new RelayServiceOptions { SharedSecret = new string('s', 16), VideoCodec = "copy" };

        var arguments = Arguments(SourceTracks.Of(hasVideo: false, hasAudio: true), options);

        Assert.DoesNotContain("copy", ValuesOf(arguments, "-c:v"));
        Assert.Contains("libx264", ValuesOf(arguments, "-c:v"));
    }

    [Fact]
    public void Turning_synthesis_off_sends_exactly_what_the_source_carries()
    {
        var options = new RelayServiceOptions
        {
            SharedSecret = new string('s', 16),
            SynthesizeMissingTracks = false,
        };

        // The worker reads the option before inspecting at all, so a disabled deployment never
        // even runs the probe; argument building then sees an unknown source.
        var arguments = Arguments(SourceTracks.Unknown, options);

        Assert.Single(ValuesOf(arguments, "-i"));
        Assert.DoesNotContain("lavfi", arguments);
    }

    [Fact]
    public void A_source_carrying_neither_kind_generates_nothing()
    {
        // Not reachable from the inspector, which reports an empty answer as unknown — but if it
        // ever became reachable, generating one half would map both output streams to the single
        // generated input and the encoder would fail at startup, hiding the real problem: there is
        // no broadcast to relay.
        var arguments = Arguments(SourceTracks.Of(hasVideo: false, hasAudio: false));

        Assert.Single(ValuesOf(arguments, "-i"));
        Assert.DoesNotContain("lavfi", arguments);
        Assert.DoesNotContain("-map", arguments);
    }

    [Fact]
    public void The_input_url_is_the_one_it_was_handed()
    {
        // Guards a subtle refactor hazard: the URL is now built once, before the source is
        // inspected, so that the probe and the encoder cannot end up reading different paths.
        var arguments = Arguments(SourceTracks.Of(hasVideo: true, hasAudio: true));

        Assert.Equal(SourceUrl, ValuesOf(arguments, "-i").Single());
    }

    private sealed class StubInspector(SourceTracks tracks) : ISourceInspector
    {
        public Task<SourceTracks> InspectAsync(string sourceUrl, CancellationToken cancellationToken) =>
            Task.FromResult(tracks);
    }
}

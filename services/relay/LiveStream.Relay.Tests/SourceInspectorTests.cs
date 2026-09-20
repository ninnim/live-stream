using Xunit;

namespace LiveStream.Relay.Tests;

/// <summary>
/// Reading ffprobe's answer.
///
/// The stakes are asymmetric, which is what shapes every case here. Reporting audio as absent when
/// it is present replaces a broadcaster's voice with silence; reporting it as present when it is
/// absent leaves the platform showing nothing, which is what happened before any of this existed.
/// So anything short of a clear answer has to come back as unknown.
/// </summary>
public sealed class SourceInspectorTests
{
    [Fact]
    public void A_camera_and_microphone_source_reports_both()
    {
        var tracks = FfprobeSourceInspector.Parse("video\naudio\n");

        Assert.Equal(SourceTrackState.Present, tracks.Video);
        Assert.Equal(SourceTrackState.Present, tracks.Audio);
        Assert.False(tracks.NeedsSilentAudio);
        Assert.False(tracks.NeedsBlankVideo);
    }

    [Fact]
    public void A_screen_share_with_no_microphone_reports_audio_absent()
    {
        var tracks = FfprobeSourceInspector.Parse("video\n");

        Assert.Equal(SourceTrackState.Absent, tracks.Audio);
        Assert.True(tracks.NeedsSilentAudio);
        Assert.False(tracks.NeedsBlankVideo);
    }

    [Fact]
    public void A_microphone_with_no_picture_reports_video_absent()
    {
        var tracks = FfprobeSourceInspector.Parse("audio\n");

        Assert.Equal(SourceTrackState.Absent, tracks.Video);
        Assert.True(tracks.NeedsBlankVideo);
        Assert.False(tracks.NeedsSilentAudio);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n  \n")]
    public void An_empty_answer_is_unknown_rather_than_no_tracks(string output)
    {
        // A successful probe always names at least one stream. Nothing means the probe did not
        // really succeed, and reading it as "no audio" would synthesise silence over a broadcast
        // that has sound.
        var tracks = FfprobeSourceInspector.Parse(output);

        Assert.Equal(SourceTracks.Unknown, tracks);
        Assert.False(tracks.NeedsSilentAudio);
        Assert.False(tracks.NeedsBlankVideo);
    }

    [Fact]
    public void Extra_streams_do_not_confuse_the_answer()
    {
        // Sources carry data and timecode streams; only the two kinds an RTMP platform needs are
        // being asked about.
        var tracks = FfprobeSourceInspector.Parse("video\ndata\naudio\nsubtitle\n");

        Assert.Equal(SourceTrackState.Present, tracks.Video);
        Assert.Equal(SourceTrackState.Present, tracks.Audio);
    }

    [Fact]
    public void Windows_line_endings_are_read_the_same_way()
    {
        var tracks = FfprobeSourceInspector.Parse("video\r\naudio\r\n");

        Assert.Equal(SourceTrackState.Present, tracks.Video);
        Assert.Equal(SourceTrackState.Present, tracks.Audio);
    }

    [Fact]
    public void An_unknown_source_never_reads_as_a_reason_to_change_anything()
    {
        Assert.False(SourceTracks.Unknown.NeedsSilentAudio);
        Assert.False(SourceTracks.Unknown.NeedsBlankVideo);
    }

    [Fact]
    public void Neither_kind_present_asks_for_neither_to_be_generated()
    {
        // One kind has to be real for the other to be worth generating: there is no broadcast to
        // carry a generated track on.
        var nothing = SourceTracks.Of(hasVideo: false, hasAudio: false);

        Assert.False(nothing.NeedsSilentAudio);
        Assert.False(nothing.NeedsBlankVideo);
    }

    [Fact]
    public void The_description_is_safe_to_log()
    {
        // It goes into a log line beside a destination id. The source URL carries the gateway read
        // credential, so nothing derived from it may appear here.
        var description = SourceTracks.Of(hasVideo: true, hasAudio: false).Describe();

        Assert.Equal("video=Present, audio=Absent", description);
    }
}

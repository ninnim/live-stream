using LiveStream.Domain.Common;
using LiveStream.Domain.Studio;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// Rules the studio's configuration enforces for itself
/// (docs/07-live-studio.md, implementation/phase-5-professional-live-studio.md).
/// </summary>
public class SessionBrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private const string TinyPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static SessionBranding Create() => SessionBranding.CreateDefault(Guid.NewGuid(), Now);

    [Fact]
    public void A_new_session_starts_unbranded_but_not_unstyled()
    {
        var branding = Create();

        Assert.Null(branding.LogoDataUri);
        Assert.False(branding.ShowLogo);
        Assert.Equal(SessionBranding.DefaultAccentColor, branding.AccentColor);
    }

    [Fact]
    public void Setting_a_logo_turns_the_watermark_on()
    {
        var branding = Create();

        branding.SetLogo(TinyPng, Now);

        Assert.Equal(TinyPng, branding.LogoDataUri);
        Assert.True(branding.ShowLogo);
    }

    [Fact]
    public void Clearing_the_logo_turns_the_watermark_off()
    {
        // Otherwise the session reports that it is drawing a watermark it does not have.
        var branding = Create();
        branding.SetLogo(TinyPng, Now);

        branding.SetLogo(null, Now);

        Assert.Null(branding.LogoDataUri);
        Assert.False(branding.ShowLogo);
    }

    [Fact]
    public void Showing_a_logo_that_does_not_exist_is_refused_quietly()
    {
        var branding = Create();

        branding.Update(LogoPosition.TopLeft, 100, "#FFFFFF", showLogo: true, Now);

        Assert.False(branding.ShowLogo);
    }

    [Theory]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("https://example.test/logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
    public void Only_raster_image_data_is_accepted_as_a_logo(string candidate)
    {
        // This value is drawn into the composition canvas. SVG is excluded along with the rest:
        // it is a document format that can carry script, not just pixels.
        var branding = Create();

        var error = Assert.Throws<DomainException>(() => branding.SetLogo(candidate, Now));
        Assert.Equal(ErrorCodes.ValidationFailed, error.ErrorCode);
    }

    [Fact]
    public void An_oversized_logo_is_refused()
    {
        var branding = Create();
        var huge = "data:image/png;base64," + new string('A', SessionBranding.MaxLogoBytes);

        Assert.Throws<DomainException>(() => branding.SetLogo(huge, Now));
    }

    [Theory]
    [InlineData("#fff", "#FFF")]
    [InlineData("#0ea5e9", "#0EA5E9")]
    [InlineData("  #123456  ", "#123456")]
    public void An_accent_colour_is_normalised(string input, string expected)
    {
        Assert.Equal(expected, SessionBranding.NormalizeColor(input));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("")]
    [InlineData("#fff; background: url(x)")]
    public void An_accent_colour_that_is_not_hex_is_refused(string candidate)
    {
        // The value reaches a canvas fill style, and the frame it renders is broadcast.
        Assert.Throws<DomainException>(() => SessionBranding.NormalizeColor(candidate));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Opacity_outside_zero_to_one_hundred_is_refused(int opacity)
    {
        var branding = Create();

        Assert.Throws<DomainException>(() =>
            branding.Update(LogoPosition.TopRight, opacity, "#FFFFFF", false, Now));
    }
}

public class SessionSceneTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static SessionScene Create(SceneLayout layout = SceneLayout.Solo, Guid? primary = null,
        Guid? secondary = null) =>
        SessionScene.Create(Guid.NewGuid(), "Opening", layout, primary, secondary, null, null, 0, Now);

    [Fact]
    public void A_scene_needs_a_name()
    {
        Assert.Throws<DomainException>(() =>
            SessionScene.Create(Guid.NewGuid(), "   ", SceneLayout.Solo, null, null, null, null, 0, Now));
    }

    [Fact]
    public void A_scene_cannot_show_the_same_source_twice()
    {
        var source = Guid.NewGuid();

        Assert.Throws<DomainException>(() => Create(SceneLayout.SideBySide, source, source));
    }

    [Fact]
    public void A_solo_scene_forgets_a_second_source_rather_than_holding_one_it_cannot_show()
    {
        var scene = Create(SceneLayout.Solo, Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(scene.SecondarySourceId);
    }

    [Fact]
    public void Captions_are_trimmed_and_blank_ones_become_absent()
    {
        var scene = SessionScene.Create(Guid.NewGuid(), "Interview", SceneLayout.Solo, null, null,
            "  Ada Lovelace  ", "   ", 0, Now);

        Assert.Equal("Ada Lovelace", scene.LowerThirdTitle);
        Assert.Null(scene.LowerThirdSubtitle);
    }

    [Fact]
    public void A_long_caption_is_truncated_rather_than_rejected()
    {
        // A caption is typed under time pressure; refusing to save one because it is two characters
        // too long is worse than shortening it.
        var scene = SessionScene.Create(Guid.NewGuid(), "Interview", SceneLayout.Solo, null, null,
            new string('x', 500), null, 0, Now);

        Assert.Equal(SessionScene.MaxCaptionLength, scene.LowerThirdTitle!.Length);
    }

    [Fact]
    public void Forgetting_a_source_clears_only_the_slots_that_named_it()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var scene = Create(SceneLayout.SideBySide, kept, removed);

        Assert.True(scene.ForgetSource(removed, Now));

        Assert.Equal(kept, scene.PrimarySourceId);
        Assert.Null(scene.SecondarySourceId);
    }

    [Fact]
    public void Forgetting_a_source_the_scene_never_named_changes_nothing()
    {
        var scene = Create(SceneLayout.Solo, Guid.NewGuid());

        Assert.False(scene.ForgetSource(Guid.NewGuid(), Now));
    }

    [Fact]
    public void An_unknown_layout_is_refused()
    {
        var scene = Create();

        Assert.Throws<DomainException>(() =>
            scene.Update("Opening", (SceneLayout)99, null, null, null, null, Now));
    }
}

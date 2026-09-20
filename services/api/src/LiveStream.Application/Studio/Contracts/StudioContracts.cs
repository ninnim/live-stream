using LiveStream.Domain.Studio;

namespace LiveStream.Application.Studio.Contracts;

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

// A null logo clears the watermark. Absent from the request and the current one is kept — the two
// are distinguished so that saving a colour change does not silently discard the logo.
public sealed record UpdateBrandingRequest(
    string? LogoDataUri,
    bool ReplaceLogo,
    string? LogoPosition,
    int? LogoOpacityPercent,
    string? AccentColor,
    bool? ShowLogo);

public sealed record SaveSceneRequest(
    string Name,
    string Layout,
    Guid? PrimarySourceId,
    Guid? SecondarySourceId,
    string? LowerThirdTitle,
    string? LowerThirdSubtitle);

// ---------------------------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------------------------

public sealed record BrandingResponse(
    Guid LiveSessionId,
    string? LogoDataUri,
    string LogoPosition,
    int LogoOpacityPercent,
    string AccentColor,
    bool ShowLogo,
    DateTimeOffset UpdatedAt);

public sealed record SceneResponse(
    Guid Id,
    Guid LiveSessionId,
    string Name,
    string Layout,
    Guid? PrimarySourceId,
    Guid? SecondarySourceId,
    string? LowerThirdTitle,
    string? LowerThirdSubtitle,
    int Position,
    DateTimeOffset UpdatedAt);

// ---------------------------------------------------------------------------------------------
// Mapping
// ---------------------------------------------------------------------------------------------

public static class StudioMapper
{
    public static BrandingResponse ToResponse(SessionBranding branding) => new(
        branding.LiveSessionId,
        branding.LogoDataUri,
        branding.LogoPosition.ToString(),
        branding.LogoOpacityPercent,
        branding.AccentColor,
        branding.ShowLogo,
        branding.UpdatedAt);

    public static SceneResponse ToResponse(SessionScene scene) => new(
        scene.Id,
        scene.LiveSessionId,
        scene.Name,
        scene.Layout.ToString(),
        scene.PrimarySourceId,
        scene.SecondarySourceId,
        scene.LowerThirdTitle,
        scene.LowerThirdSubtitle,
        scene.Position,
        scene.UpdatedAt);
}

using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Studio;

/// <summary>Where a watermark sits in the frame.</summary>
public enum LogoPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// The look a session carries: a watermark and an accent colour, drawn over every composed frame
/// (docs/07-live-studio.md, "Branding").
///
/// One per session, created with it, so the studio never has to reason about branding being absent.
/// </summary>
public class SessionBranding
{
    /// <summary>
    /// Cap on the stored logo.
    ///
    /// The image is held as a data URI rather than in object storage, which keeps the platform free
    /// of file-upload infrastructure and — the reason that matters here — keeps the image
    /// same-origin. A cross-origin image drawn into a canvas taints it, and `captureStream` on a
    /// tainted canvas throws, which would take the whole broadcast down rather than fail to draw a
    /// logo. 256 KB is generous for a watermark and small enough to sit in a row.
    /// </summary>
    public const int MaxLogoBytes = 256 * 1024;

    private static readonly string[] AllowedLogoPrefixes =
    [
        "data:image/png;base64,",
        "data:image/jpeg;base64,",
        "data:image/webp;base64,",
    ];

    private SessionBranding()
    {
        AccentColor = DefaultAccentColor;
    }

    /// <summary>Matches the studio's own accent, so an unbranded session still looks deliberate.</summary>
    public const string DefaultAccentColor = "#0EA5E9";

    public Guid LiveSessionId { get; private set; }

    /// <summary>A PNG, JPEG or WebP data URI. Null when the session has no watermark.</summary>
    public string? LogoDataUri { get; private set; }

    public LogoPosition LogoPosition { get; private set; } = LogoPosition.TopRight;

    /// <summary>0 to 100. A watermark that cannot be dimmed is a watermark nobody uses.</summary>
    public int LogoOpacityPercent { get; private set; } = 80;

    /// <summary>Hex, including the leading hash. Used for the lower-third accent bar.</summary>
    public string AccentColor { get; private set; }

    public bool ShowLogo { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public LiveSession? LiveSession { get; set; }

    public static SessionBranding CreateDefault(Guid liveSessionId, DateTimeOffset now) => new()
    {
        LiveSessionId = liveSessionId,
        AccentColor = DefaultAccentColor,
        LogoPosition = LogoPosition.TopRight,
        LogoOpacityPercent = 80,
        ShowLogo = false,
        UpdatedAt = now,
    };

    /// <summary>
    /// Replaces the watermark.
    ///
    /// Passing null clears it, which also turns the watermark off: leaving <see cref="ShowLogo"/>
    /// set with nothing to draw would report a state the frame does not have.
    /// </summary>
    public void SetLogo(string? logoDataUri, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(logoDataUri))
        {
            LogoDataUri = null;
            ShowLogo = false;
            UpdatedAt = now;
            return;
        }

        var trimmed = logoDataUri.Trim();

        if (!AllowedLogoPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "A logo must be a PNG, JPEG or WebP image.");
        }

        // Measured on the encoded string, which is what actually gets stored and sent.
        if (trimmed.Length > MaxLogoBytes)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"A logo must be smaller than {MaxLogoBytes / 1024} KB.");
        }

        LogoDataUri = trimmed;
        ShowLogo = true;
        UpdatedAt = now;
    }

    public void Update(LogoPosition position, int opacityPercent, string accentColor, bool showLogo,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(position))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown logo position.");
        }

        if (opacityPercent is < 0 or > 100)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Opacity must be between 0 and 100.");
        }

        LogoPosition = position;
        LogoOpacityPercent = opacityPercent;
        AccentColor = NormalizeColor(accentColor);

        // Asking to show a logo that does not exist is a request the frame cannot honour.
        ShowLogo = showLogo && LogoDataUri is not null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Accepts `#RGB` and `#RRGGBB`, upper or lower case, and refuses anything else.
    ///
    /// This value is written straight into a canvas fill style. Anything unvalidated there is
    /// attacker-controlled input reaching a rendering context, and the frame it renders is
    /// broadcast to an audience.
    /// </summary>
    public static string NormalizeColor(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        var valid = trimmed.Length is 4 or 7
                    && trimmed[0] == '#'
                    && trimmed[1..].All(Uri.IsHexDigit);

        if (!valid)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "An accent colour must be a hex value such as #0EA5E9.");
        }

        return trimmed.ToUpperInvariant();
    }
}

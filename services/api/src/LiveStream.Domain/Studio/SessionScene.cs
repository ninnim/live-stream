using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;

namespace LiveStream.Domain.Studio;

/// <summary>How the sources in a scene are arranged. Mirrors the studio's layouts exactly.</summary>
public enum SceneLayout
{
    Solo,
    SideBySide,
    PictureInPicture,
}

/// <summary>
/// A prepared shot: which sources, in what arrangement, with what caption
/// (docs/07-live-studio.md, "Scenes").
///
/// Scenes are configuration, not state. Recalling one asks the studio to arrange itself; it does not
/// itself put anything on air, because promoting a source has its own rules — a source that has
/// stopped sending cannot go on air, and a scene saved an hour ago knows nothing about that.
/// </summary>
public class SessionScene
{
    public const int MaxNameLength = 60;
    public const int MaxCaptionLength = 120;

    /// <summary>
    /// Enough for a show, few enough to pick from under time pressure. A wall of forty scenes is
    /// slower to use than no scenes at all.
    /// </summary>
    public const int MaxScenesPerSession = 12;

    private SessionScene()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid LiveSessionId { get; private set; }

    public string Name { get; private set; }

    public SceneLayout Layout { get; private set; }

    /// <summary>
    /// The source this scene puts on air. Held as a plain id rather than a relation: a scene must
    /// survive the source being revoked, and recalling it should say "that camera has gone" rather
    /// than vanish along with it.
    /// </summary>
    public Guid? PrimarySourceId { get; private set; }

    /// <summary>The second source in a two-source layout. Ignored by <see cref="SceneLayout.Solo"/>.</summary>
    public Guid? SecondarySourceId { get; private set; }

    public string? LowerThirdTitle { get; private set; }

    public string? LowerThirdSubtitle { get; private set; }

    /// <summary>Display order in the control room. Scenes are chosen by position under pressure.</summary>
    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public LiveSession? LiveSession { get; set; }

    public static SessionScene Create(Guid liveSessionId, string name, SceneLayout layout,
        Guid? primarySourceId, Guid? secondarySourceId, string? lowerThirdTitle,
        string? lowerThirdSubtitle, int position, DateTimeOffset now)
    {
        var scene = new SessionScene
        {
            Id = Guid.NewGuid(),
            LiveSessionId = liveSessionId,
            Position = position,
            CreatedAt = now,
        };

        scene.Update(name, layout, primarySourceId, secondarySourceId, lowerThirdTitle,
            lowerThirdSubtitle, now);

        return scene;
    }

    public void Update(string name, SceneLayout layout, Guid? primarySourceId, Guid? secondarySourceId,
        string? lowerThirdTitle, string? lowerThirdSubtitle, DateTimeOffset now)
    {
        Name = RequireName(name);

        if (!Enum.IsDefined(layout))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown scene layout.");
        }

        // A two-source layout showing the same camera twice is a mistake every time, not a choice.
        if (secondarySourceId is not null && secondarySourceId == primarySourceId)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "A scene cannot show the same source twice.");
        }

        Layout = layout;
        PrimarySourceId = primarySourceId;
        SecondarySourceId = layout == SceneLayout.Solo ? null : secondarySourceId;
        LowerThirdTitle = Clean(lowerThirdTitle, MaxCaptionLength);
        LowerThirdSubtitle = Clean(lowerThirdSubtitle, MaxCaptionLength);
        UpdatedAt = now;
    }

    public void MoveTo(int position, DateTimeOffset now)
    {
        if (position < 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A scene position cannot be negative.");
        }

        Position = position;
        UpdatedAt = now;
    }

    /// <summary>Forgets a source that has been removed, so the scene stays usable rather than broken.</summary>
    public bool ForgetSource(Guid sourceId, DateTimeOffset now)
    {
        var changed = false;

        if (PrimarySourceId == sourceId)
        {
            PrimarySourceId = null;
            changed = true;
        }

        if (SecondarySourceId == sourceId)
        {
            SecondarySourceId = null;
            changed = true;
        }

        if (changed) UpdatedAt = now;
        return changed;
    }

    private static string RequireName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A scene needs a name.");
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

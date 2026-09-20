using LiveStream.Application.Abstractions;
using LiveStream.Application.Sessions;
using LiveStream.Application.Studio.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Studio;
using Microsoft.EntityFrameworkCore;

namespace LiveStream.Application.Studio;

/// <summary>
/// The studio's look and its prepared shots
/// (docs/07-live-studio.md, implementation/phase-5-professional-live-studio.md).
///
/// Everything here is *configuration*, never live state. Recalling a scene tells the control room
/// how to arrange itself; it does not put anything on air, because promoting a source has rules of
/// its own that a scene saved an hour ago knows nothing about.
/// </summary>
public sealed class StudioService(
    IAppDbContext db,
    LiveSessionAuthorizationService authorization,
    IClock clock)
{
    // -----------------------------------------------------------------------------------------
    // Branding
    // -----------------------------------------------------------------------------------------

    public async Task<BrandingResponse> GetBrandingAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (_, branding) = await LoadBrandingAsync(sessionId, userId, WorkspacePermission.LiveSessionView,
            cancellationToken);

        return StudioMapper.ToResponse(branding);
    }

    public async Task<BrandingResponse> UpdateBrandingAsync(Guid sessionId, Guid userId,
        UpdateBrandingRequest request, CancellationToken cancellationToken)
    {
        var (_, branding) = await LoadBrandingAsync(sessionId, userId, WorkspacePermission.LiveSessionEdit,
            cancellationToken);

        var now = clock.UtcNow;

        // Only touched when the caller says so. Otherwise saving a colour change from a form that
        // does not carry the image would quietly delete the logo.
        if (request.ReplaceLogo)
        {
            branding.SetLogo(request.LogoDataUri, now);
        }

        branding.Update(
            ParsePosition(request.LogoPosition) ?? branding.LogoPosition,
            request.LogoOpacityPercent ?? branding.LogoOpacityPercent,
            request.AccentColor ?? branding.AccentColor,
            request.ShowLogo ?? branding.ShowLogo,
            now);

        await db.SaveChangesAsync(cancellationToken);

        return StudioMapper.ToResponse(branding);
    }

    // -----------------------------------------------------------------------------------------
    // Scenes
    // -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SceneResponse>> ListScenesAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView,
            cancellationToken);

        var scenes = await db.SessionScenes
            .AsNoTracking()
            .Where(scene => scene.LiveSessionId == sessionId)
            .OrderBy(scene => scene.Position)
            .ToListAsync(cancellationToken);

        return scenes.Select(StudioMapper.ToResponse).ToList();
    }

    public async Task<SceneResponse> AddSceneAsync(Guid sessionId, Guid userId, SaveSceneRequest request,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionWithScenesAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionEdit,
            cancellationToken);

        await EnsureSourcesBelongAsync(sessionId, request, cancellationToken);

        var scene = session.AddScene(request.Name, ParseLayout(request.Layout), request.PrimarySourceId,
            request.SecondarySourceId, request.LowerThirdTitle, request.LowerThirdSubtitle, clock.UtcNow);

        db.SessionScenes.Add(scene);
        await db.SaveChangesAsync(cancellationToken);

        return StudioMapper.ToResponse(scene);
    }

    public async Task<SceneResponse> UpdateSceneAsync(Guid sessionId, Guid sceneId, Guid userId,
        SaveSceneRequest request, CancellationToken cancellationToken)
    {
        var scene = await LoadSceneAsync(sessionId, sceneId, userId, cancellationToken);
        await EnsureSourcesBelongAsync(sessionId, request, cancellationToken);

        scene.Update(request.Name, ParseLayout(request.Layout), request.PrimarySourceId,
            request.SecondarySourceId, request.LowerThirdTitle, request.LowerThirdSubtitle, clock.UtcNow);

        await db.SaveChangesAsync(cancellationToken);

        return StudioMapper.ToResponse(scene);
    }

    public async Task DeleteSceneAsync(Guid sessionId, Guid sceneId, Guid userId,
        CancellationToken cancellationToken)
    {
        var scene = await LoadSceneAsync(sessionId, sceneId, userId, cancellationToken);

        db.SessionScenes.Remove(scene);
        await db.SaveChangesAsync(cancellationToken);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static SceneLayout ParseLayout(string? value)
    {
        if (!Enum.TryParse<SceneLayout>(value, ignoreCase: true, out var layout) || !Enum.IsDefined(layout))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown scene layout '{value}'.");
        }

        return layout;
    }

    private static LogoPosition? ParsePosition(string? value)
    {
        if (value is null) return null;

        if (!Enum.TryParse<LogoPosition>(value, ignoreCase: true, out var position) || !Enum.IsDefined(position))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown logo position '{value}'.");
        }

        return position;
    }

    /// <summary>
    /// Refuses a scene that names a source from another session.
    ///
    /// Source ids are stored without a foreign key so that a scene outlives the camera it names, so
    /// nothing in the database would otherwise stop one session's scene referencing another's.
    /// </summary>
    private async Task EnsureSourcesBelongAsync(Guid sessionId, SaveSceneRequest request,
        CancellationToken cancellationToken)
    {
        var ids = new[] { request.PrimarySourceId, request.SecondarySourceId }
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return;

        var found = await db.SessionSources
            .CountAsync(source => source.LiveSessionId == sessionId && ids.Contains(source.Id),
                cancellationToken);

        if (found != ids.Count)
        {
            throw new DomainException(ErrorCodes.SourceNotFound,
                "A scene can only use sources from its own session.");
        }
    }

    private async Task<LiveSession> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
        ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

    private async Task<LiveSession> LoadSessionWithScenesAsync(Guid sessionId,
        CancellationToken cancellationToken) =>
        await db.LiveSessions.Include(s => s.Scenes).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
        ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

    private async Task<(LiveSession Session, SessionBranding Branding)> LoadBrandingAsync(Guid sessionId,
        Guid userId, WorkspacePermission permission, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
            .Include(s => s.Branding)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        await authorization.EnsureAllowedAsync(session, userId, permission, cancellationToken);

        return (session, session.Branding);
    }

    private async Task<SessionScene> LoadSceneAsync(Guid sessionId, Guid sceneId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionEdit,
            cancellationToken);

        var scene = await db.SessionScenes.FirstOrDefaultAsync(s => s.Id == sceneId, cancellationToken)
                    ?? throw new DomainException(ErrorCodes.SceneNotFound, "Scene not found.");

        // Checking the parent rather than trusting the route: a scene id from one session must not
        // be operable through another the caller does have access to.
        if (scene.LiveSessionId != sessionId)
        {
            throw new DomainException(ErrorCodes.SceneNotFound, "Scene not found.");
        }

        return scene;
    }
}

using LiveStream.Domain.Common;
using LiveStream.Domain.Media;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sources;
using LiveStream.Domain.Studio;

namespace LiveStream.Domain.Sessions;

/// <summary>
/// The central domain aggregate (ai/architecture-rules.md). Owns its lifecycle: state may only
/// change through <see cref="TransitionTo"/>, which enforces the state machine and appends an event.
/// </summary>
public class LiveSession
{
    private readonly List<LiveSessionEvent> _events = [];
    private readonly List<Recording> _recordings = [];
    private readonly List<IngestCredential> _ingestCredentials = [];
    private readonly List<SessionSource> _sources = [];
    private readonly List<SessionScene> _scenes = [];

    // EF Core materialisation constructor.
    private LiveSession()
    {
        Title = string.Empty;
        MediaPathName = string.Empty;
    }

    private LiveSession(Guid workspaceId, Guid createdByUserId, string title, string? description,
        LiveSessionVisibility visibility, bool recordingEnabled, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        Title = title;
        Description = description;
        Visibility = visibility;
        RecordingEnabled = recordingEnabled;
        MediaPathName = IdGenerator.NewMediaPathName();
        Status = LiveSessionStatus.Draft;
        StateEnteredAt = now;
        CreatedAt = now;
        UpdatedAt = now;
        Health = new LiveSessionHealth { LiveSessionId = Id };
        Branding = SessionBranding.CreateDefault(Id, now);
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public LiveSessionStatus Status { get; private set; }

    public LiveSessionVisibility Visibility { get; private set; }

    public bool RecordingEnabled { get; private set; }

    /// <summary>
    /// Unguessable media-plane path for this session. Deliberately not the database id, so playback
    /// URLs cannot be enumerated and the path can be rotated without changing session identity.
    /// </summary>
    public string MediaPathName { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// When the session entered its current <see cref="Status"/>. Drives the start-ingest timeout and
    /// the bounded recovery window, which must not be affected by unrelated row updates.
    /// </summary>
    public DateTimeOffset StateEnteredAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token; prevents lost updates from racing start/stop calls.</summary>
    public int Version { get; private set; }

    public LiveSessionHealth Health { get; private set; } = null!;

    /// <summary>The look this session broadcasts with. Created with the session, never absent.</summary>
    public SessionBranding Branding { get; private set; } = null!;

    public IReadOnlyCollection<LiveSessionEvent> Events => _events;

    public IReadOnlyCollection<Recording> Recordings => _recordings;

    public IReadOnlyCollection<IngestCredential> IngestCredentials => _ingestCredentials;

    /// <summary>Devices and participants contributing to this session (docs/05-multi-device.md).</summary>
    public IReadOnlyCollection<SessionSource> Sources => _sources;

    /// <summary>Prepared shots the control room can recall (docs/07-live-studio.md).</summary>
    public IReadOnlyCollection<SessionScene> Scenes => _scenes;

    public static LiveSession Create(Guid workspaceId, Guid createdByUserId, string title,
        string? description, LiveSessionVisibility visibility, bool recordingEnabled, DateTimeOffset now)
    {
        var normalisedTitle = (title ?? string.Empty).Trim();
        if (normalisedTitle.Length is 0 or > 200)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Session title is required and must be 200 characters or fewer.");
        }

        var session = new LiveSession(workspaceId, createdByUserId, normalisedTitle, description?.Trim(),
            visibility, recordingEnabled, now);

        session.RecordEvent(LiveSessionEventType.Created, now, createdByUserId,
            detail: $"visibility={visibility}; recording={recordingEnabled}");

        // Every session has a studio source from the outset, and it publishes to the session's own
        // path — so a single-camera broadcast behaves exactly as it did before multi-device existed.
        // Additional devices get their own paths and are previewed alongside it.
        session.AddSource(SessionSource.CreateHost(session.Id, session.MediaPathName, createdByUserId, now));

        return session;
    }

    /// <summary>
    /// Applies a validated state change. This is the only way <see cref="Status"/> can change.
    /// Repeating the current state is a no-op so retried requests and duplicate media callbacks
    /// stay idempotent (docs/09 API rules).
    /// </summary>
    public void TransitionTo(LiveSessionStatus target, DateTimeOffset now, Guid? actorUserId = null,
        string? reason = null, string? errorCode = null, string? correlationId = null)
    {
        if (Status == target)
        {
            return;
        }

        LiveSessionStateMachine.EnsureCanTransition(Status, target);

        var previous = Status;
        Status = target;
        StateEnteredAt = now;
        UpdatedAt = now;

        if (target is LiveSessionStatus.Live && StartedAt is null)
        {
            StartedAt = now;
        }
        else if (target is LiveSessionStatus.Ended or LiveSessionStatus.Failed)
        {
            EndedAt = now;
        }

        if (errorCode is not null)
        {
            LastErrorCode = errorCode;
            LastErrorMessage = reason;
        }
        else if (target is LiveSessionStatus.Live)
        {
            LastErrorCode = null;
            LastErrorMessage = null;
        }

        _events.Add(new LiveSessionEvent
        {
            LiveSessionId = Id,
            Type = MapEventType(target),
            FromStatus = previous,
            ToStatus = target,
            ErrorCode = errorCode,
            Detail = reason,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        });
    }

    public void UpdateDetails(string title, string? description, LiveSessionVisibility visibility,
        bool recordingEnabled, DateTimeOffset now)
    {
        if (Status is not (LiveSessionStatus.Draft or LiveSessionStatus.Ready))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "Session details can only be edited before the broadcast starts.");
        }

        var normalisedTitle = (title ?? string.Empty).Trim();
        if (normalisedTitle.Length is 0 or > 200)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Session title is required and must be 200 characters or fewer.");
        }

        Title = normalisedTitle;
        Description = description?.Trim();
        Visibility = visibility;
        RecordingEnabled = recordingEnabled;
        UpdatedAt = now;
    }

    public LiveSessionEvent RecordEvent(LiveSessionEventType type, DateTimeOffset now, Guid? actorUserId = null,
        string? detail = null, string? errorCode = null, string? correlationId = null)
    {
        var domainEvent = new LiveSessionEvent
        {
            LiveSessionId = Id,
            Type = type,
            FromStatus = Status,
            ToStatus = null,
            ErrorCode = errorCode,
            Detail = detail,
            CorrelationId = correlationId,
            ActorUserId = actorUserId,
            CreatedAt = now,
        };

        _events.Add(domainEvent);
        return domainEvent;
    }

    public void AddRecording(Recording recording)
    {
        recording.LiveSessionId = Id;
        _recordings.Add(recording);
    }

    public void AddIngestCredential(IngestCredential credential)
    {
        credential.LiveSessionId = Id;
        _ingestCredentials.Add(credential);
    }

    public void AddSource(SessionSource source)
    {
        source.LiveSessionId = Id;
        _sources.Add(source);
    }

    /// <summary>
    /// Saves a prepared shot.
    ///
    /// The limit is a usability constraint rather than a technical one: a wall of scenes is slower
    /// to pick from under time pressure than none at all.
    /// </summary>
    public SessionScene AddScene(string name, SceneLayout layout, Guid? primarySourceId,
        Guid? secondarySourceId, string? lowerThirdTitle, string? lowerThirdSubtitle, DateTimeOffset now)
    {
        if (_scenes.Count >= SessionScene.MaxScenesPerSession)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"A session can hold {SessionScene.MaxScenesPerSession} scenes. Remove one first.");
        }

        var position = _scenes.Count == 0 ? 0 : _scenes.Max(scene => scene.Position) + 1;

        var scene = SessionScene.Create(Id, name, layout, primarySourceId, secondarySourceId,
            lowerThirdTitle, lowerThirdSubtitle, position, now);

        _scenes.Add(scene);
        UpdatedAt = now;

        return scene;
    }

    public void RemoveScene(SessionScene scene, DateTimeOffset now)
    {
        _scenes.Remove(scene);
        UpdatedAt = now;
    }

    /// <summary>
    /// Drops a removed source out of every scene that referenced it.
    ///
    /// Without this, revoking a camera leaves scenes pointing at something that no longer exists,
    /// and recalling one silently does nothing — which reads as the scene being broken rather than
    /// as the camera having gone.
    /// </summary>
    public IReadOnlyList<SessionScene> ForgetSourceInScenes(Guid sourceId, DateTimeOffset now) =>
        _scenes.Where(scene => scene.ForgetSource(sourceId, now)).ToList();

    /// <summary>Rotates the media path so a leaked ingest/playback path can be invalidated.</summary>
    public void RotateMediaPath(DateTimeOffset now)
    {
        MediaPathName = IdGenerator.NewMediaPathName();
        UpdatedAt = now;
    }

    private static LiveSessionEventType MapEventType(LiveSessionStatus target) => target switch
    {
        LiveSessionStatus.Ready => LiveSessionEventType.Prepared,
        LiveSessionStatus.Live => LiveSessionEventType.Started,
        LiveSessionStatus.Ended => LiveSessionEventType.Stopped,
        LiveSessionStatus.Failed => LiveSessionEventType.Failed,
        LiveSessionStatus.Reconnecting => LiveSessionEventType.ReconnectAttempted,
        _ => LiveSessionEventType.StateChanged,
    };
}

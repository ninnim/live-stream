namespace LiveStream.Domain.Sources;

/// <summary>
/// What a contributing device is for (docs/05-multi-device.md "Device roles").
///
/// The role decides what a paired device may do, and nothing else about it is negotiable by the
/// device — it is assigned by the operator when the pairing code is created, not chosen by whoever
/// redeems it.
/// </summary>
public enum SourceRole
{
    /// <summary>The main broadcaster. Every session has exactly one, created with the session.</summary>
    Host = 0,

    /// <summary>An additional camera, typically a phone.</summary>
    Camera = 1,

    /// <summary>A screen or window share.</summary>
    Screen = 2,

    /// <summary>Audio only — a wireless mic or a remote commentator.</summary>
    Audio = 3,

    /// <summary>Moderates chat. Contributes no media.</summary>
    Moderator = 4,

    /// <summary>Operates the show — switching program, managing devices. Contributes no media.</summary>
    Operator = 5,

    /// <summary>Watches the program feed for quality control. Contributes no media.</summary>
    ViewerMonitor = 6,
}

/// <summary>Lifecycle of one contributing source within a session.</summary>
public enum SourceStatus
{
    /// <summary>A pairing code exists but nothing has redeemed it yet.</summary>
    Invited = 0,

    /// <summary>Redeemed and authenticated, but not yet sending media.</summary>
    Paired = 1,

    /// <summary>Media is arriving from this source.</summary>
    Connected = 2,

    /// <summary>Media stopped. The device may still be paired and can resume.</summary>
    Disconnected = 3,

    /// <summary>Access withdrawn. Terminal — a revoked device must pair again from a new code.</summary>
    Revoked = 4,
}

/// <summary>
/// Capabilities a role grants. Kept as pure data, in the same shape as
/// <see cref="LiveStream.Domain.Identity.WorkspacePermissions"/>, so device authorization is unit
/// testable and cannot drift between endpoints.
/// </summary>
public enum SourcePermission
{
    /// <summary>Publish media into this source's own ingest path.</summary>
    PublishMedia = 0,

    /// <summary>Read the session's status and source list.</summary>
    ViewSession = 1,

    /// <summary>Choose which source is on air.</summary>
    SwitchProgram = 2,

    /// <summary>Create pairing codes and revoke other devices.</summary>
    ManageDevices = 3,

    /// <summary>Moderate chat.</summary>
    ModerateChat = 4,
}

public static class SourcePermissions
{
    private static readonly IReadOnlyDictionary<SourceRole, IReadOnlySet<SourcePermission>> Grants =
        new Dictionary<SourceRole, IReadOnlySet<SourcePermission>>
        {
            [SourceRole.Host] = Set(
                SourcePermission.PublishMedia,
                SourcePermission.ViewSession,
                SourcePermission.SwitchProgram,
                SourcePermission.ManageDevices),

            // A contributing camera publishes and sees the session. It deliberately cannot switch
            // program or manage devices: handing a borrowed phone the ability to cut the show, or
            // to revoke the operator, is not a capability anyone intends to grant.
            [SourceRole.Camera] = Set(
                SourcePermission.PublishMedia,
                SourcePermission.ViewSession),

            [SourceRole.Screen] = Set(
                SourcePermission.PublishMedia,
                SourcePermission.ViewSession),

            [SourceRole.Audio] = Set(
                SourcePermission.PublishMedia,
                SourcePermission.ViewSession),

            [SourceRole.Moderator] = Set(
                SourcePermission.ViewSession,
                SourcePermission.ModerateChat),

            [SourceRole.Operator] = Set(
                SourcePermission.ViewSession,
                SourcePermission.SwitchProgram,
                SourcePermission.ManageDevices),

            [SourceRole.ViewerMonitor] = Set(
                SourcePermission.ViewSession),
        };

    public static bool Allows(SourceRole role, SourcePermission permission) =>
        Grants.TryGetValue(role, out var permissions) && permissions.Contains(permission);

    public static IReadOnlySet<SourcePermission> For(SourceRole role) =>
        Grants.TryGetValue(role, out var permissions) ? permissions : Set();

    /// <summary>True when the role is expected to send media, and therefore needs an ingest path.</summary>
    public static bool ContributesMedia(SourceRole role) => Allows(role, SourcePermission.PublishMedia);

    private static IReadOnlySet<SourcePermission> Set(params SourcePermission[] values) =>
        new HashSet<SourcePermission>(values);
}

/// <summary>Auditable events recorded against one source.</summary>
public enum SourceEventType
{
    Invited = 0,
    Paired = 1,
    Connected = 2,
    Disconnected = 3,
    Revoked = 4,
    Renamed = 5,
    PromotedToProgram = 6,
    RemovedFromProgram = 7,
    PairingExpired = 8,
    PairingRejected = 9,
    CredentialIssued = 10,
}

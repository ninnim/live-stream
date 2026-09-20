namespace LiveStream.Domain.Identity;

/// <summary>
/// Workspace roles, ordered from most to least privileged (MASTER_BLUEPRINT.md §31).
/// Phase 1 uses the subset needed for broadcasting; the remaining roles exist so later phases
/// do not need a breaking permission migration.
/// </summary>
public enum WorkspaceRole
{
    Owner = 0,
    Admin = 1,
    Producer = 2,
    Host = 3,
    Moderator = 4,
    Analyst = 5,
    Viewer = 6,
}

/// <summary>Resource-scoped permissions checked by the API layer (MASTER_BLUEPRINT.md §31).</summary>
public enum WorkspacePermission
{
    LiveSessionView = 0,
    LiveSessionEdit = 1,
    LiveSessionStart = 2,
    LiveSessionStop = 3,
    RecordingView = 4,
    AnalyticsView = 5,

    /// <summary>
    /// Add, edit, and remove distribution destinations, and link provider accounts.
    /// Separate from <see cref="LiveSessionEdit"/> because it grants custody of long-lived platform
    /// secrets: someone who may run a broadcast is not automatically someone who may point it at a
    /// new channel (MASTER_BLUEPRINT.md §31 DESTINATION_MANAGE).
    /// </summary>
    DestinationManage = 6,

    /// <summary>
    /// Invite devices to a session, revoke them, and choose which source is on air.
    /// Separate from <see cref="DestinationManage"/>: admitting a phone to one broadcast is a
    /// per-session production decision, not custody of the workspace's platform credentials
    /// (docs/05-multi-device.md).
    /// </summary>
    SourceManage = 7,

    /// <summary>
    /// Change what the workspace itself is: its plan overrides, its data residency, its retention
    /// policy, its identity provider, and its data — export and erasure included.
    ///
    /// Held only by Owner and Admin. Everything behind it either sets a security boundary or
    /// destroys data, which is a different kind of authority from running a broadcast
    /// (implementation/phase-7-scale-security-and-globalization.md).
    /// </summary>
    WorkspaceManage = 8,
}

public class WorkspaceMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public WorkspaceRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }

    public User? User { get; set; }
}

/// <summary>
/// Central role-to-permission mapping. Kept as pure data so authorization rules are unit testable
/// and cannot drift between endpoints.
/// </summary>
public static class WorkspacePermissions
{
    private static readonly IReadOnlyDictionary<WorkspaceRole, IReadOnlySet<WorkspacePermission>> Grants =
        new Dictionary<WorkspaceRole, IReadOnlySet<WorkspacePermission>>
        {
            [WorkspaceRole.Owner] = All(),
            [WorkspaceRole.Admin] = All(),
            [WorkspaceRole.Producer] = Set(
                WorkspacePermission.LiveSessionView,
                WorkspacePermission.LiveSessionEdit,
                WorkspacePermission.LiveSessionStart,
                WorkspacePermission.LiveSessionStop,
                WorkspacePermission.RecordingView,
                WorkspacePermission.AnalyticsView,
                WorkspacePermission.DestinationManage,
                WorkspacePermission.SourceManage),
            // Host deliberately excludes DestinationManage: running a broadcast does not imply
            // custody of the workspace's platform credentials. It does include SourceManage,
            // because bringing a second camera into your own show is part of running it.
            [WorkspaceRole.Host] = Set(
                WorkspacePermission.LiveSessionView,
                WorkspacePermission.LiveSessionEdit,
                WorkspacePermission.LiveSessionStart,
                WorkspacePermission.LiveSessionStop,
                WorkspacePermission.RecordingView,
                WorkspacePermission.SourceManage),
            [WorkspaceRole.Moderator] = Set(
                WorkspacePermission.LiveSessionView),
            [WorkspaceRole.Analyst] = Set(
                WorkspacePermission.LiveSessionView,
                WorkspacePermission.RecordingView,
                WorkspacePermission.AnalyticsView),
            [WorkspaceRole.Viewer] = Set(
                WorkspacePermission.LiveSessionView),
        };

    public static bool Allows(WorkspaceRole role, WorkspacePermission permission) =>
        Grants.TryGetValue(role, out var permissions) && permissions.Contains(permission);

    public static IReadOnlySet<WorkspacePermission> For(WorkspaceRole role) =>
        Grants.TryGetValue(role, out var permissions) ? permissions : Set();

    private static IReadOnlySet<WorkspacePermission> All() =>
        new HashSet<WorkspacePermission>(Enum.GetValues<WorkspacePermission>());

    private static IReadOnlySet<WorkspacePermission> Set(params WorkspacePermission[] values) =>
        new HashSet<WorkspacePermission>(values);
}

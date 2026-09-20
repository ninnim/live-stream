using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LiveStream.Application.Sessions;

/// <summary>
/// Single place where "may this user do this to this session?" is answered. Every session
/// mutation routes through here so no endpoint can forget the check (docs/11-security.md).
/// </summary>
public sealed class LiveSessionAuthorizationService(IAppDbContext db)
{
    /// <summary>
    /// Resolves the caller's role in a workspace, or <c>null</c> when they are not a member.
    /// </summary>
    public async Task<WorkspaceRole?> GetRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId)
            .Select(m => (WorkspaceRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

        return membership;
    }

    /// <summary>
    /// Throws when the caller lacks <paramref name="permission"/> on the session's workspace.
    /// Non-membership and insufficient role both surface as the same error so the endpoint cannot
    /// be used to probe which sessions exist in other workspaces.
    /// </summary>
    public async Task EnsureAllowedAsync(LiveSession session, Guid userId, WorkspacePermission permission,
        CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(session.WorkspaceId, userId, cancellationToken);
        if (role is null || !WorkspacePermissions.Allows(role.Value, permission))
        {
            throw new DomainException(ErrorCodes.PermissionDenied,
                "You do not have access to this live session.");
        }
    }

    /// <summary>
    /// Viewer-side access for playback. Public and unlisted sessions are readable by anyone holding
    /// the link; private sessions require workspace membership.
    /// </summary>
    public async Task<bool> CanViewPlaybackAsync(LiveSession session, Guid? userId, CancellationToken cancellationToken)
    {
        if (session.Visibility is LiveSessionVisibility.Public or LiveSessionVisibility.Unlisted)
        {
            return true;
        }

        if (userId is not { } id)
        {
            return false;
        }

        var role = await GetRoleAsync(session.WorkspaceId, id, cancellationToken);
        return role is not null && WorkspacePermissions.Allows(role.Value, WorkspacePermission.LiveSessionView);
    }
}

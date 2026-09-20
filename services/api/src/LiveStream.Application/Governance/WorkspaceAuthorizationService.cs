using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace LiveStream.Application.Governance;

/// <summary>
/// Answers "may this user do this to this <em>workspace</em>?".
///
/// Separate from <see cref="Sessions.LiveSessionAuthorizationService"/>, which starts from a
/// session and finds its workspace. Governance operations have no session to start from: they act
/// on the tenant itself.
/// </summary>
public sealed class WorkspaceAuthorizationService(IAppDbContext db)
{
    public async Task<WorkspaceRole?> GetRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId)
            .Select(m => (WorkspaceRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Throws unless the caller holds <paramref name="permission"/> in the workspace.
    ///
    /// A workspace that does not exist and one the caller is not in produce the same error, so the
    /// endpoint cannot be used to discover which workspace ids are real.
    /// </summary>
    public async Task EnsureAllowedAsync(Guid workspaceId, Guid userId, WorkspacePermission permission,
        CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(workspaceId, userId, cancellationToken);

        if (role is null || !WorkspacePermissions.Allows(role.Value, permission))
        {
            throw new DomainException(ErrorCodes.WorkspaceNotFound, "Workspace not found.");
        }
    }
}

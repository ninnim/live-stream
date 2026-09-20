using LiveStream.Api.Security;
using LiveStream.Application.Sessions;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveStream.Api.Realtime;

/// <summary>
/// Realtime control channel for the Live Studio (docs/09-api-specification.md, hub <c>/hubs/live</c>).
///
/// Membership of a session group is authorized on join, so subscribing to another workspace's
/// session is not possible even with a valid token. The hub only pushes server-authoritative state;
/// clients cannot use it to assert session state.
/// </summary>
[Authorize]
public sealed class LiveSessionHub(
    LiveSessionService sessions,
    LiveSessionAuthorizationService authorization,
    ILogger<LiveSessionHub> logger) : Hub
{
    public static string GroupName(Guid sessionId) => $"live-session:{sessionId}";

    /// <summary>Subscribes the caller to a session's events after verifying access.</summary>
    public async Task<LiveSessionStatusResponse> JoinSession(Guid sessionId)
    {
        var userId = Context.User?.GetUserId()
                     ?? throw new HubException(ErrorCodes.AuthenticationFailed);

        var session = await sessions.LoadAsync(sessionId, Context.ConnectionAborted);

        try
        {
            await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView,
                Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Rejected hub join for session {SessionId} user {UserId}", sessionId, userId);
            throw new HubException(ex.ErrorCode);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId), Context.ConnectionAborted);
        logger.LogInformation("Hub client joined session {SessionId} user {UserId}", sessionId, userId);

        return await sessions.GetStatusAsync(sessionId, userId, Context.ConnectionAborted);
    }

    public Task LeaveSession(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId), Context.ConnectionAborted);

    /// <summary>
    /// Liveness ping from the studio. Returns server time so the client can display connection
    /// state without inferring it from the absence of messages.
    /// </summary>
    public DateTimeOffset Heartbeat() => DateTimeOffset.UtcNow;
}

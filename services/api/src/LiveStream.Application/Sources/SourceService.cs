using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Governance;
using LiveStream.Application.Sessions;
using LiveStream.Application.Sources.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Media;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Sources;

/// <summary>
/// Admits devices to a Live Session and manages them once they are in
/// (docs/05-multi-device.md, implementation/phase-4-multi-device-and-collaboration.md).
///
/// The rule the whole class exists to honour: a device never holds a permanent credential. It
/// redeems a short-lived, single-use code for a short-lived, source-scoped token, and revoking it
/// erases both and severs its media connection in the same breath.
/// </summary>
public sealed class SourceService(
    IAppDbContext db,
    IMediaGateway mediaGateway,
    LiveSessionAuthorizationService authorization,
    TenantLimitService tenantLimits,
    ILiveSessionNotifier notifier,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<SourceOptions> options,
    ILogger<SourceService> logger)
{
    private readonly SourceOptions _options = options.Value;

    // -----------------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SourceResponse>> ListAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);

        var sources = await db.SessionSources
            .AsNoTracking()
            .Where(s => s.LiveSessionId == sessionId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return sources.Select(SourceMapper.ToResponse).ToList();
    }

    public async Task<IReadOnlyList<SourceEventResponse>> ListEventsAsync(Guid sessionId, Guid sourceId,
        Guid userId, int limit, CancellationToken cancellationToken)
    {
        await LoadForOperatorAsync(sessionId, sourceId, userId, WorkspacePermission.LiveSessionView, cancellationToken);

        var events = await db.SourceEvents
            .AsNoTracking()
            .Where(e => e.SessionSourceId == sourceId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return events.Select(SourceMapper.ToResponse).ToList();
    }

    /// <summary>
    /// Issues a short-lived way for the control room to watch one source.
    ///
    /// The read token matters for private sessions: path visibility alone would deny the read, and
    /// without it a producer could not see their own second camera.
    /// </summary>
    public async Task<SourcePreviewResponse> GetPreviewAsync(Guid sessionId, Guid sourceId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (session, source) = await LoadForOperatorAsync(sessionId, sourceId, userId,
            WorkspacePermission.LiveSessionView, cancellationToken);

        if (!SourcePermissions.ContributesMedia(source.Role))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"A {source.Role} source sends no media, so there is nothing to preview.");
        }

        var now = clock.UtcNow;
        var (credential, plaintext) = IngestCredential.Issue(session.Id, userId, source.MediaPathName,
            IngestCredentialScope.Read, now, _options.PreviewCredentialLifetime);

        db.IngestCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);

        var endpoints = mediaGateway.DescribeEndpoints(source.MediaPathName);

        return new SourcePreviewResponse(
            source.Id,
            endpoints.WebRtcPlaybackUrl,
            endpoints.HlsPlaybackUrl,
            plaintext,
            credential.ExpiresAt);
    }

    // -----------------------------------------------------------------------------------------
    // Invitation and pairing
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Invites a device and returns the pairing code once.
    ///
    /// The code is generated, hashed, and the plaintext handed straight back — it is never stored
    /// and cannot be retrieved again. Losing it means issuing a new invitation, which is the
    /// intended behaviour.
    /// </summary>
    public async Task<SourceInvitationResponse> InviteAsync(Guid sessionId, Guid userId,
        InviteSourceRequest request, string joinBaseUrl, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.SourceManage, cancellationToken);

        if (LiveSessionStateMachine.IsTerminal(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "This session has finished. Devices can only join a session that has not ended.");
        }

        var role = ParseRole(request.Role);

        // Revoked sources still occupy a row for the audit trail but not a seat at the table.
        var limits = await tenantLimits.ResolveAsync(session.WorkspaceId, cancellationToken);
        var activeCount = await db.SessionSources
            .CountAsync(s => s.LiveSessionId == sessionId && s.Status != SourceStatus.Revoked, cancellationToken);

        if (activeCount >= limits.MaxSourcesPerSession)
        {
            throw new DomainException(ErrorCodes.SourceLimitReached,
                $"A session on the {limits.Plan} plan can have at most {limits.MaxSourcesPerSession} sources.");
        }

        var now = clock.UtcNow;
        var (source, code) = SessionSource.Invite(session.Id, role, request.DisplayName,
            IdGenerator.NewMediaPathName(), userId, now, _options.PairingCodeLifetime);

        db.SessionSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);

        // Role and source id are safe to log. The code is not, and never appears in any log or event.
        logger.LogInformation(
            "Device invited to session {SessionId}: source={SourceId} role={Role} correlationId={CorrelationId}",
            sessionId, source.Id, role, correlation.CorrelationId);

        await SafeNotifyAsync(source, cancellationToken);

        var expiresAt = now.Add(_options.PairingCodeLifetime);

        return new SourceInvitationResponse(
            SourceMapper.ToResponse(source),
            SessionSource.FormatCode(code),
            BuildJoinUrl(joinBaseUrl, code),
            expiresAt,
            _options.PairingCodeLifetimeSeconds);
    }

    /// <summary>
    /// Redeems a pairing code. Anonymous by necessity — the device has no account.
    ///
    /// Every failure returns the same error, whether the code was never real, has expired, or was
    /// already used. Distinguishing them would turn this endpoint into an oracle for guessing.
    /// </summary>
    public async Task<DevicePairedResponse> ClaimAsync(ClaimPairingRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = SessionSource.NormalizeCode(request.Code);

        if (normalized.Length == 0)
        {
            throw InvalidCode();
        }

        var hash = SessionSource.HashSecret(normalized);

        var source = await db.SessionSources
            .Include(s => s.LiveSession)
            .FirstOrDefaultAsync(s => s.PairingCodeHash == hash, cancellationToken);

        if (source is null)
        {
            logger.LogWarning("Pairing rejected: unknown code correlationId={CorrelationId}",
                correlation.CorrelationId);
            throw InvalidCode();
        }

        var session = source.LiveSession
                      ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        if (LiveSessionStateMachine.IsTerminal(session.Status))
        {
            source.RecordEvent(SourceEventType.PairingRejected, clock.UtcNow,
                detail: "Session already ended.", correlationId: correlation.CorrelationId);
            await db.SaveChangesAsync(cancellationToken);

            throw new DomainException(ErrorCodes.SessionNotReady, "That session has already ended.");
        }

        string token;
        try
        {
            token = source.Claim(clock.UtcNow, _options.DeviceTokenLifetime, request.DeviceLabel);
        }
        catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.CredentialExpired)
        {
            source.RecordEvent(SourceEventType.PairingExpired, clock.UtcNow,
                correlationId: correlation.CorrelationId);
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidCode();
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Device paired session={SessionId} source={SourceId} role={Role} correlationId={CorrelationId}",
            session.Id, source.Id, source.Role, correlation.CorrelationId);

        await SafeNotifyAsync(source, cancellationToken);

        return new DevicePairedResponse(
            token,
            source.DeviceTokenExpiresAt ?? clock.UtcNow,
            session.Id,
            session.Title,
            SourceMapper.ToResponse(source));
    }

    /// <summary>
    /// Resolves a presented device token to its source. Returns <c>null</c> for anything unusable,
    /// which includes a revoked device — that is what makes revocation take effect on the very next
    /// request rather than when a token happens to expire.
    /// </summary>
    public async Task<SessionSource?> ResolveDeviceAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return null;
        }

        var hash = SessionSource.HashSecret(presentedToken);

        var source = await db.SessionSources
            .FirstOrDefaultAsync(s => s.DeviceTokenHash == hash, cancellationToken);

        return source is not null && source.IsDeviceTokenUsableAt(clock.UtcNow) ? source : null;
    }

    // -----------------------------------------------------------------------------------------
    // Device-side operations
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Loads a source that is still entitled to act. Returns <c>null</c> once it has been revoked,
    /// so a request authenticated a moment before revocation still fails.
    /// </summary>
    public async Task<SessionSource?> GetActiveSourceAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await db.SessionSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        return source is not null && source.IsDeviceTokenUsableAt(clock.UtcNow) ? source : null;
    }

    /// <summary>What a paired device may know about the session it joined.</summary>
    public async Task<DeviceSessionResponse> DescribeForDeviceAsync(SessionSource source,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(source.LiveSessionId, cancellationToken);

        return new DeviceSessionResponse(
            session.Id,
            session.Title,
            session.Status.ToString(),
            LiveSessionStateMachine.IsBroadcasting(session.Status),
            SourceMapper.ToResponse(source),
            SourcePermissions.For(source.Role).Select(p => p.ToString()).ToList());
    }

    /// <summary>
    /// Mints a publish credential for a device's own ingest path.
    ///
    /// Scoped to that path and no other, so a paired camera cannot publish over the studio feed or
    /// into another session — the same guarantee the studio's own credential has.
    /// </summary>
    public async Task<(IngestCredential Credential, string Plaintext, MediaEndpoints Endpoints)>
        IssueDeviceCredentialAsync(SessionSource source, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (!source.Allows(SourcePermission.PublishMedia))
        {
            throw new DomainException(ErrorCodes.PermissionDenied,
                $"A {source.Role} source is not permitted to publish media.");
        }

        var session = await LoadSessionAsync(source.LiveSessionId, cancellationToken);

        if (!LiveSessionStateMachine.ExpectsIngest(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "The session is not ready to receive media yet.");
        }

        var tracked = await db.SessionSources.FirstAsync(s => s.Id == source.Id, cancellationToken);
        var now = clock.UtcNow;

        var (credential, plaintext) = IngestCredential.Issue(session.Id, tracked.CreatedByUserId,
            tracked.MediaPathName, IngestCredentialScope.Publish, now, lifetime);

        db.IngestCredentials.Add(credential);
        tracked.RecordEvent(SourceEventType.CredentialIssued, now,
            detail: $"scope=PUBLISH ttlSeconds={(int)lifetime.TotalSeconds}",
            correlationId: correlation.CorrelationId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Device ingest credential issued source={SourceId} credentialId={CredentialId}",
            tracked.Id, credential.Id);

        return (credential, plaintext, mediaGateway.DescribeEndpoints(tracked.MediaPathName));
    }

    // -----------------------------------------------------------------------------------------
    // Operator management
    // -----------------------------------------------------------------------------------------

    public async Task<SourceResponse> RenameAsync(Guid sessionId, Guid sourceId, Guid userId,
        RenameSourceRequest request, CancellationToken cancellationToken)
    {
        var (_, source) = await LoadForOperatorAsync(sessionId, sourceId, userId,
            WorkspacePermission.SourceManage, cancellationToken);

        source.Rename(request.DisplayName, clock.UtcNow, userId);
        await db.SaveChangesAsync(cancellationToken);
        await SafeNotifyAsync(source, cancellationToken);

        return SourceMapper.ToResponse(source);
    }

    /// <summary>
    /// Puts one source on air, taking whichever source held the program off it.
    ///
    /// A session has exactly one program at a time, and the swap is committed in a single
    /// transaction: two sources both believing they are on air is a state the control room has no
    /// way to render honestly, and it would survive until someone noticed.
    ///
    /// This records the operator's decision. It does not move media — the studio composes the
    /// program in the browser and publishes the result, so what viewers see follows from this flag
    /// rather than from any change to the media plane
    /// (docs/decisions/0012-program-switching-and-composition.md).
    /// </summary>
    public async Task<IReadOnlyList<SourceResponse>> SetProgramAsync(Guid sessionId, Guid sourceId,
        Guid userId, CancellationToken cancellationToken)
    {
        var (_, source) = await LoadForOperatorAsync(sessionId, sourceId, userId,
            WorkspacePermission.SourceManage, cancellationToken);

        if (source.IsProgram)
        {
            return [SourceMapper.ToResponse(source)];
        }

        var now = clock.UtcNow;

        // Loaded before the promotion so that a rejected promotion — a source that is not
        // connected, say — leaves the previous program untouched rather than taking the session
        // off air on the way to failing.
        var outgoing = await db.SessionSources
            .Where(s => s.LiveSessionId == sessionId && s.IsProgram && s.Id != sourceId)
            .ToListAsync(cancellationToken);

        source.PromoteToProgram(now, userId, correlation.CorrelationId);

        foreach (var previous in outgoing)
        {
            previous.RemoveFromProgram(now, userId, correlation.CorrelationId);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Program changed session={SessionId} source={SourceId} replaced={ReplacedCount} correlationId={CorrelationId}",
            sessionId, sourceId, outgoing.Count, correlation.CorrelationId);

        // Both ends of the swap are announced, so a second control room updates its highlight
        // rather than showing two sources on air until it next polls.
        foreach (var changed in outgoing.Append(source))
        {
            await SafeNotifyAsync(changed, cancellationToken);
        }

        return outgoing.Append(source).Select(SourceMapper.ToResponse).ToList();
    }

    /// <summary>
    /// Withdraws a device immediately.
    ///
    /// Three things happen together, and all three are needed: the credentials are erased, every
    /// outstanding ingest credential for that path is revoked, and whatever is currently publishing
    /// is severed at the gateway. Without the last one, a revoked phone keeps streaming until its
    /// credential expires — which is not what anyone means by "revoked".
    /// </summary>
    public async Task<SourceResponse> RevokeAsync(Guid sessionId, Guid sourceId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (_, source) = await LoadForOperatorAsync(sessionId, sourceId, userId,
            WorkspacePermission.SourceManage, cancellationToken);

        if (source.Role is SourceRole.Host)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "The studio source cannot be revoked; stop the session instead.");
        }

        var now = clock.UtcNow;
        var mediaPath = source.MediaPathName;

        source.Revoke(now, userId, "Revoked by operator.");

        if (!string.IsNullOrEmpty(mediaPath))
        {
            var credentials = await db.IngestCredentials
                .Where(c => c.MediaPathName == mediaPath && c.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var credential in credentials)
            {
                credential.Revoke(now);
            }
        }

        // Scenes name sources by id and hold no foreign key, so a revoked camera would otherwise
        // leave scenes pointing at something that no longer exists — and recalling one would
        // quietly do nothing, which reads as the scene being broken rather than the camera gone.
        // Committed in the same transaction as the revocation: a scene must never outlive its
        // source's removal.
        var scenes = await db.SessionScenes
            .Where(scene => scene.LiveSessionId == sessionId
                            && (scene.PrimarySourceId == sourceId || scene.SecondarySourceId == sourceId))
            .ToListAsync(cancellationToken);

        foreach (var scene in scenes)
        {
            scene.ForgetSource(sourceId, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(mediaPath))
        {
            try
            {
                var kicked = await mediaGateway.KickPublisherAsync(mediaPath, cancellationToken);
                logger.LogInformation(
                    "Source revoked session={SessionId} source={SourceId} publisherKicked={Kicked} correlationId={CorrelationId}",
                    sessionId, sourceId, kicked, correlation.CorrelationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The revocation itself has already been committed, so the device cannot obtain a
                // new credential. An unreachable gateway delays severing an existing connection; it
                // must not roll back the revocation.
                logger.LogError(ex,
                    "Revoked source {SourceId} but could not sever its publisher at the gateway", sourceId);
            }
        }

        await SafeNotifyAsync(source, cancellationToken);

        return SourceMapper.ToResponse(source);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static DomainException InvalidCode() => new(ErrorCodes.PairingCodeInvalid,
        "That pairing code is not valid. Ask for a new one.");

    private static SourceRole ParseRole(string? value)
    {
        if (!Enum.TryParse<SourceRole>(value, ignoreCase: true, out var role))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown source role '{value}'.");
        }

        return role;
    }

    /// <summary>Builds the URL a QR code encodes. The code is a path segment, never a query string.</summary>
    private static string BuildJoinUrl(string joinBaseUrl, string code) =>
        $"{joinBaseUrl.TrimEnd('/')}/join/{Uri.EscapeDataString(code)}";

    private async Task<LiveSession> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
        ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

    private async Task<(LiveSession Session, SessionSource Source)> LoadForOperatorAsync(Guid sessionId,
        Guid sourceId, Guid userId, WorkspacePermission permission, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, permission, cancellationToken);

        var source = await db.SessionSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken)
                     ?? throw new DomainException(ErrorCodes.SourceNotFound, "Source not found.");

        // Checking the parent rather than trusting the route: without this, a source id from one
        // session could be operated on through another session the caller does have access to.
        if (source.LiveSessionId != sessionId)
        {
            throw new DomainException(ErrorCodes.SourceNotFound, "Source not found.");
        }

        return (session, source);
    }

    private async Task SafeNotifyAsync(SessionSource source, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.SourceStateChangedAsync(
                SourceMapper.ToStatusResponse(source, clock.UtcNow), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Realtime is a latency optimisation; the control room's polling still reflects truth.
            logger.LogWarning(ex, "Publishing source state for {SourceId} failed", source.Id);
        }
    }
}

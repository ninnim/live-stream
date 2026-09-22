using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Sessions;
using LiveStream.Application.Sessions.Contracts;
using LiveStream.Domain.Common;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Media;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Media;

/// <summary>Action a media-plane client is attempting, as reported by the gateway's auth hook.</summary>
public enum MediaAccessAction
{
    Publish,
    Read,
}

public sealed record MediaAccessRequest(string MediaPathName, string? Token, MediaAccessAction Action, string? ClientIp);

/// <summary>
/// Issues and validates the short-lived, scoped credentials that let a browser publish into a
/// session. No permanent stream key ever reaches the client, and the media gateway delegates every
/// authorization decision back to this service (docs/11-security.md, docs/03-streaming-engine.md).
/// </summary>
public sealed class IngestCredentialService(
    IAppDbContext db,
    IMediaGateway mediaGateway,
    LiveSessionAuthorizationService authorization,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<LiveSessionOptions> options,
    IOptions<IceOptions> iceOptions,
    ILogger<IngestCredentialService> logger)
{
    private readonly LiveSessionOptions _options = options.Value;
    private readonly IceOptions _ice = iceOptions.Value;

    /// <summary>
    /// Mints a publish credential for the caller. The plaintext token is returned once, here, and
    /// is never persisted or logged.
    /// </summary>
    public async Task<IngestCredentialResponse> IssueAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
                          .Include(s => s.Health)
                          .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                      ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        // Credentials are only useful while the media plane is holding a path open for the session.
        if (!LiveSessionStateMachine.ExpectsIngest(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "Prepare the session before requesting broadcaster credentials.");
        }

        var now = clock.UtcNow;
        var (credential, plaintext) = IngestCredential.Issue(session.Id, userId, session.MediaPathName,
            IngestCredentialScope.Publish, now, _options.IngestCredentialLifetime);

        db.IngestCredentials.Add(credential);
        session.RecordEvent(LiveSessionEventType.CredentialIssued, now, userId,
            detail: $"scope=PUBLISH ttlSeconds={_options.IngestCredentialLifetimeSeconds}",
            correlationId: correlation.CorrelationId);

        await db.SaveChangesAsync(cancellationToken);

        // Credential id is safe to log; the token and its hash are not.
        logger.LogInformation(
            "Ingest credential issued session={SessionId} credentialId={CredentialId} ttlSeconds={Ttl} correlationId={CorrelationId}",
            session.Id, credential.Id, _options.IngestCredentialLifetimeSeconds, correlation.CorrelationId);

        var endpoints = mediaGateway.DescribeEndpoints(session.MediaPathName);

        // ICE configuration travels with the credential: it is issued per connection attempt to an
        // authorized caller, which is exactly the handling any TURN credential needs.
        var iceServers = _ice.Servers
            .Where(server => server.Urls.Count > 0)
            .Select(server => new IceServerResponse(server.Urls, server.Username, server.Credential))
            .ToList();

        return new IngestCredentialResponse(
            endpoints.IngestProtocol,
            endpoints.IngestUrl,
            plaintext,
            credential.ExpiresAt,
            _options.IngestCredentialLifetimeSeconds,
            iceServers);
    }

    /// <summary>
    /// Issues — or rotates — the stream key an external encoder publishes with.
    /// </summary>
    /// <remarks>
    /// This is the path that lets somebody broadcast a phone game, a console through a capture
    /// card, or an OBS scene: all three are encoders that speak RTMP and none of them can run a
    /// browser's credential handshake (docs/decisions/0022-external-encoder-ingest.md).
    ///
    /// Three deliberate differences from <see cref="IssueAsync"/>:
    ///
    /// <list type="bullet">
    /// <item>It does not require the session to be expecting ingest yet. The key has to be
    /// obtainable *before* going live, because it has to be typed into an encoder first. Publishing
    /// with it still checks that, in <see cref="AuthorizeMediaAccessAsync"/> — the gate stays, it
    /// just moves to the moment that matters.</item>
    /// <item>Issuing revokes every previous key for the session. That makes this button both
    /// "show me my key" and "the old one is compromised", which is the only way somebody who has
    /// pasted a key into the wrong window can fix it themselves.</item>
    /// <item>It refuses once a session is over. A key for a finished session could never publish
    /// anything, and handing one out would only suggest otherwise.</item>
    /// </list>
    /// </remarks>
    public async Task<StreamKeyResponse> IssueStreamKeyAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
                          .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                      ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        var endpoints = mediaGateway.DescribeEndpoints(session.MediaPathName);

        if (endpoints.RtmpIngestUrl is null)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "External encoder ingest is not enabled on this deployment.");
        }

        if (LiveSessionStateMachine.IsTerminal(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "This session has ended. Start a new one to stream from an encoder.");
        }

        var now = clock.UtcNow;

        // Rotation: the previous key stops working the instant a new one is shown. Anything else
        // would leave a leaked key alive for its full lifetime with no way to kill it.
        var replaced = await RevokeStreamKeysInternalAsync(session.Id, now, cancellationToken);

        var (credential, plaintext) = IngestCredential.Issue(session.Id, userId, session.MediaPathName,
            IngestCredentialScope.StreamKey, now, _options.StreamKeyLifetime);

        db.IngestCredentials.Add(credential);
        session.RecordEvent(LiveSessionEventType.CredentialIssued, now, userId,
            detail: $"scope=STREAM_KEY ttlSeconds={_options.StreamKeyLifetimeSeconds} replaced={replaced}",
            correlationId: correlation.CorrelationId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stream key issued session={SessionId} credentialId={CredentialId} replaced={Replaced} ttlSeconds={Ttl} correlationId={CorrelationId}",
            session.Id, credential.Id, replaced, _options.StreamKeyLifetimeSeconds, correlation.CorrelationId);

        return BuildStreamKeyResponse(endpoints, session.MediaPathName, plaintext, credential.ExpiresAt);
    }

    /// <summary>
    /// Revokes the session's encoder keys, leaving browser credentials alone.
    ///
    /// Separate from <see cref="RevokeAllAsync"/> on purpose: "stop that encoder" and "cut off all
    /// access to this session" are different intentions, and an operator reaching for the first
    /// should not silently get the second while they are live from the studio.
    /// </summary>
    public async Task<int> RevokeStreamKeysAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions
                          .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                      ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionStart, cancellationToken);

        var now = clock.UtcNow;
        var revoked = await RevokeStreamKeysInternalAsync(sessionId, now, cancellationToken);

        if (revoked > 0)
        {
            session.RecordEvent(LiveSessionEventType.CredentialRevoked, now, userId,
                detail: $"scope=STREAM_KEY count={revoked}",
                correlationId: correlation.CorrelationId);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Revoked {Count} stream keys for session {SessionId}", revoked, sessionId);
        }

        return revoked;
    }

    private async Task<int> RevokeStreamKeysInternalAsync(Guid sessionId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.IngestCredentials
            .Where(c => c.LiveSessionId == sessionId
                        && c.Scope == IngestCredentialScope.StreamKey
                        && c.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var credential in existing)
        {
            credential.Revoke(now);
        }

        return existing.Count;
    }

    /// <summary>
    /// Assembles what an encoder is actually typed into.
    /// </summary>
    /// <remarks>
    /// The credentials travel in the RTMP URL's query string, which is how MediaMTX reads them —
    /// and how every encoder that offers a "server + stream key" pair can carry them at all, since
    /// neither field is a place to put a username.
    ///
    /// The key deliberately begins with the path, so that an encoder joining server and key with a
    /// slash produces exactly <see cref="StreamKeyResponse.FullUrl"/>. Getting that wrong is the
    /// single most common way an encoder setup fails, and it fails with "connection refused"
    /// rather than anything that points at the cause.
    /// </remarks>
    private static StreamKeyResponse BuildStreamKeyResponse(MediaEndpoints endpoints, string mediaPathName,
        string plaintext, DateTimeOffset expiresAt)
    {
        var server = endpoints.RtmpIngestUrl!;
        var key = $"{mediaPathName}?user={EncoderUser}&pass={Uri.EscapeDataString(plaintext)}";

        // SRT carries the same credentials inside its stream id instead of a query string. Offered
        // because it holds up on a lossy mobile uplink far better than RTMP, which is the whole
        // case for streaming from a phone on cellular.
        var srt = endpoints.SrtIngestUrl is null
            ? null
            : $"{endpoints.SrtIngestUrl}?streamid={Uri.EscapeDataString($"publish:{mediaPathName}:{EncoderUser}:{plaintext}")}";

        return new StreamKeyResponse(
            Protocol: "RTMP",
            ServerUrl: server,
            StreamKey: key,
            FullUrl: $"{server}/{key}",
            SrtUrl: srt,
            ExpiresAt: expiresAt,
            ExpiresInSeconds: (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds));
    }

    /// <summary>
    /// Username half of the encoder credential. The gateway forwards it untouched and this service
    /// decides nothing on it — the token in the password is the whole authorization — but RTMP and
    /// SRT both require a user component to be present.
    /// </summary>
    private const string EncoderUser = "broadcaster";

    /// <summary>
    /// Called by the media gateway before it accepts a publisher or reader. Returns <c>true</c> only
    /// when the presented token is unexpired, unrevoked, scoped to this exact path, and the session
    /// is in a state that expects ingest.
    /// </summary>
    public async Task<bool> AuthorizeMediaAccessAsync(MediaAccessRequest request, CancellationToken cancellationToken)
    {
        if (request.Action is MediaAccessAction.Read)
        {
            return await AuthorizeReadAsync(request.MediaPathName, request.Token, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            logger.LogWarning("Media publish denied: no token presented for path {MediaPath}", request.MediaPathName);
            return false;
        }

        var hash = IngestCredential.HashToken(request.Token);
        var credential = await db.IngestCredentials
            .Include(c => c.LiveSession)
            .ThenInclude(s => s!.Health)
            .FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);

        if (credential is null)
        {
            logger.LogWarning("Media publish denied: unknown credential for path {MediaPath}", request.MediaPathName);
            return false;
        }

        var now = clock.UtcNow;

        if (!credential.IsUsableAt(now))
        {
            logger.LogWarning(
                "Media publish denied: credential expired or revoked credentialId={CredentialId} session={SessionId}",
                credential.Id, credential.LiveSessionId);
            return false;
        }

        // Binding the credential to one path is what stops a token for session A publishing into B.
        if (!string.Equals(credential.MediaPathName, request.MediaPathName, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Media publish denied: path mismatch credentialId={CredentialId} requested={MediaPath}",
                credential.Id, request.MediaPathName);
            return false;
        }

        // Both scopes authorize the same action; they differ in how long they live and how they are
        // renewed, not in what they are allowed to do.
        if (credential.Scope is not (IngestCredentialScope.Publish or IngestCredentialScope.StreamKey))
        {
            logger.LogWarning("Media publish denied: wrong scope credentialId={CredentialId}", credential.Id);
            return false;
        }

        var session = credential.LiveSession;
        if (session is null || !LiveSessionStateMachine.ExpectsIngest(session.Status))
        {
            logger.LogWarning(
                "Media publish denied: session not accepting ingest session={SessionId} status={Status}",
                credential.LiveSessionId, session?.Status);
            return false;
        }

        credential.MarkUsed(now);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Media publish authorized session={SessionId} credentialId={CredentialId}",
            session.Id, credential.Id);

        return true;
    }

    /// <summary>Revokes every outstanding credential for a session, e.g. when access is withdrawn.</summary>
    public async Task RevokeAllAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var credentials = await db.IngestCredentials
            .Where(c => c.LiveSessionId == sessionId && c.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            credential.Revoke(now);
        }

        if (credentials.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Revoked {Count} ingest credentials for session {SessionId}",
                credentials.Count, sessionId);
        }
    }

    /// <summary>
    /// Mints a READ-scoped credential so the egress relay can pull a session from the media gateway.
    ///
    /// Server-to-server only: there is no endpoint that issues one of these to a browser. It exists
    /// because a private session must still be republishable to an external platform, and the
    /// visibility rule alone would deny the relay.
    /// </summary>
    public async Task<string> IssueRelayReadCredentialAsync(LiveSession session, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (credential, plaintext) = IngestCredential.Issue(session.Id, session.CreatedByUserId,
            session.MediaPathName, IngestCredentialScope.Read, now, lifetime);

        db.IngestCredentials.Add(credential);
        session.RecordEvent(LiveSessionEventType.CredentialIssued, now,
            detail: $"scope=READ ttlSeconds={(int)lifetime.TotalSeconds} purpose=relay",
            correlationId: correlation.CorrelationId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Relay read credential issued session={SessionId} credentialId={CredentialId} ttlSeconds={Ttl}",
            session.Id, credential.Id, (int)lifetime.TotalSeconds);

        return plaintext;
    }

    /// <summary>
    /// Read authorization. A valid READ-scoped credential wins; otherwise the decision falls back to
    /// session visibility, which is what makes public and unlisted links playable without a token.
    /// </summary>
    private async Task<bool> AuthorizeReadAsync(string mediaPathName, string? token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            var hash = IngestCredential.HashToken(token);
            var credential = await db.IngestCredentials
                .FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);

            if (credential is not null
                && credential.Scope is IngestCredentialScope.Read
                && credential.IsUsableAt(clock.UtcNow)
                && string.Equals(credential.MediaPathName, mediaPathName, StringComparison.Ordinal))
            {
                credential.MarkUsed(clock.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
        }

        var visibility = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.MediaPathName == mediaPathName)
            .Select(s => (LiveSessionVisibility?)s.Visibility)
            .FirstOrDefaultAsync(cancellationToken);

        // Private sessions are not served directly by the media plane; the path name itself is the
        // capability for public and unlisted sessions.
        return visibility is LiveSessionVisibility.Public or LiveSessionVisibility.Unlisted;
    }
}

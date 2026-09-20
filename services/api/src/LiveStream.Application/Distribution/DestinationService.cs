using LiveStream.Application.Abstractions;
using LiveStream.Application.Common;
using LiveStream.Application.Distribution.Contracts;
using LiveStream.Application.Governance;
using LiveStream.Application.Sessions;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Application.Distribution;

/// <summary>
/// Manages the destination configuration attached to a Live Session.
///
/// Owns everything an operator does to a destination outside a broadcast: adding, editing, removing,
/// enabling. Starting and stopping belong to <see cref="DestinationOrchestrator"/>, which keeps the
/// side that touches credentials and relays away from the side that only edits rows.
///
/// Every mutation requires <see cref="WorkspacePermission.DestinationManage"/>, which is stricter
/// than the permission to run a broadcast: a stream key is long-lived custody of someone else
/// channel, not a per-session action.
/// </summary>
public sealed class DestinationService(
    IAppDbContext db,
    LiveSessionAuthorizationService authorization,
    IDestinationProviderRegistry providers,
    TenantLimitService tenantLimits,
    ISecretProtector secretProtector,
    IClock clock,
    ICorrelationContext correlation,
    IOptions<DistributionOptions> options,
    ILogger<DestinationService> logger)
{
    private readonly DistributionOptions _options = options.Value;

    // -----------------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DestinationResponse>> ListAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.LiveSessionView, cancellationToken);

        var destinations = await db.StreamDestinations
            .AsNoTracking()
            .Include(d => d.ProviderAccount)
            .Where(d => d.LiveSessionId == sessionId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        return destinations.Select(d => DestinationMapper.ToResponse(d, now)).ToList();
    }

    public async Task<DestinationResponse> GetAsync(Guid sessionId, Guid destinationId, Guid userId,
        CancellationToken cancellationToken)
    {
        var (_, destination) = await LoadForReadAsync(sessionId, destinationId, userId, cancellationToken);
        return DestinationMapper.ToResponse(destination, clock.UtcNow);
    }

    public async Task<IReadOnlyList<DestinationEventResponse>> ListEventsAsync(Guid sessionId, Guid destinationId,
        Guid userId, int limit, CancellationToken cancellationToken)
    {
        await LoadForReadAsync(sessionId, destinationId, userId, cancellationToken);

        var events = await db.DestinationEvents
            .AsNoTracking()
            .Where(e => e.StreamDestinationId == destinationId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return events.Select(DestinationMapper.ToResponse).ToList();
    }

    /// <summary>Provider catalogue for the "add destination" form. Contains no workspace data.</summary>
    public IReadOnlyList<ProviderDescriptorResponse> DescribeProviders() =>
        providers.DescribeAll()
            .Select(d => new ProviderDescriptorResponse(
                d.Provider.ToString(),
                d.DisplayName,
                d.SupportsStreamKey,
                d.SupportsLinkedAccount,
                d.LinkedAccountConfigured,
                d.DefaultIngestUrl,
                d.StreamKeyHelp,
                d.HelpUrl))
            .ToList();

    // -----------------------------------------------------------------------------------------
    // Mutations
    // -----------------------------------------------------------------------------------------

    public async Task<DestinationResponse> CreateAsync(Guid sessionId, Guid userId,
        CreateDestinationRequest request, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, WorkspacePermission.DestinationManage, cancellationToken);

        if (LiveSessionStateMachine.IsTerminal(session.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "This session has finished. Destinations can only be added to a session that has not ended.");
        }

        var provider = ParseProvider(request.Provider);

        var limits = await tenantLimits.ResolveAsync(session.WorkspaceId, cancellationToken);
        var existingCount = await db.StreamDestinations.CountAsync(d => d.LiveSessionId == sessionId, cancellationToken);
        if (existingCount >= limits.MaxDestinationsPerSession)
        {
            throw new DomainException(ErrorCodes.DestinationLimitReached,
                $"A session on the {limits.Plan} plan can publish to at most " +
                $"{limits.MaxDestinationsPerSession} destinations.");
        }

        var destination = await BuildDestinationAsync(session, provider, request, userId, cancellationToken);

        db.StreamDestinations.Add(destination);
        await db.SaveChangesAsync(cancellationToken);

        // The provider and mode are safe to log; the key never is.
        logger.LogInformation(
            "Destination added to session {SessionId}: {DestinationId} provider={Provider} mode={Mode} correlationId={CorrelationId}",
            sessionId, destination.Id, destination.Provider, destination.CredentialMode, correlation.CorrelationId);

        return DestinationMapper.ToResponse(destination, clock.UtcNow);
    }

    public async Task<DestinationResponse> UpdateAsync(Guid sessionId, Guid destinationId, Guid userId,
        UpdateDestinationRequest request, CancellationToken cancellationToken)
    {
        var (_, destination) = await LoadForWriteAsync(sessionId, destinationId, userId, cancellationToken);
        var now = clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            destination.UpdateDisplayName(request.DisplayName, now, userId);
        }

        // A key change requires the URL too: the pair identifies one endpoint, and accepting one
        // without the other lets a key be silently pointed at the wrong host.
        if (request.StreamKey is not null)
        {
            if (string.IsNullOrWhiteSpace(request.IngestUrl))
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    "Supply the ingest URL together with a new stream key.");
            }

            if (DestinationStateMachine.IsActive(destination.Status))
            {
                throw new DomainException(ErrorCodes.SessionNotReady,
                    "Stop this destination before changing its stream key.");
            }

            destination.UpdateStreamKey(request.IngestUrl, secretProtector.Protect(request.StreamKey), now, userId);
        }
        else if (!string.IsNullOrWhiteSpace(request.IngestUrl)
                 && destination.CredentialMode == DestinationCredentialMode.StreamKey)
        {
            if (DestinationStateMachine.IsActive(destination.Status))
            {
                throw new DomainException(ErrorCodes.SessionNotReady,
                    "Stop this destination before changing its ingest URL.");
            }

            // Re-protect the existing key so the stored pair stays consistent, without the caller
            // ever having to send the secret back.
            var existingKey = DecryptStreamKey(destination);
            destination.UpdateStreamKey(request.IngestUrl, secretProtector.Protect(existingKey), now, userId);
        }

        if (request.Enabled is { } enabled)
        {
            destination.SetEnabled(enabled, now, userId);
        }

        await db.SaveChangesAsync(cancellationToken);

        return DestinationMapper.ToResponse(destination, now);
    }

    public async Task DeleteAsync(Guid sessionId, Guid destinationId, Guid userId, CancellationToken cancellationToken)
    {
        var (_, destination) = await LoadForWriteAsync(sessionId, destinationId, userId, cancellationToken);

        if (DestinationStateMachine.IsActive(destination.Status))
        {
            throw new DomainException(ErrorCodes.SessionNotReady,
                "Stop this destination before removing it.");
        }

        db.StreamDestinations.Remove(destination);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Destination removed from session {SessionId}: {DestinationId} correlationId={CorrelationId}",
            sessionId, destinationId, correlation.CorrelationId);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private async Task<StreamDestination> BuildDestinationAsync(LiveSession session, DestinationProvider provider,
        CreateDestinationRequest request, Guid userId, CancellationToken cancellationToken)
    {
        var descriptor = providers.Get(provider).Describe();
        var now = clock.UtcNow;

        var hasStreamKey = !string.IsNullOrWhiteSpace(request.StreamKey);
        var hasAccount = request.ProviderAccountId is not null;

        if (hasStreamKey == hasAccount)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Supply either a stream key or a linked account, not both and not neither.");
        }

        if (hasStreamKey)
        {
            if (!descriptor.SupportsStreamKey)
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"{descriptor.DisplayName} destinations cannot be configured with a stream key.");
            }

            // The platform publishes a fixed endpoint for most providers; the operator only has to
            // supply one when there is no sensible default (custom RTMP).
            var ingestUrl = string.IsNullOrWhiteSpace(request.IngestUrl)
                ? descriptor.DefaultIngestUrl
                : request.IngestUrl;

            if (string.IsNullOrWhiteSpace(ingestUrl))
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    "An ingest URL is required for this provider.");
            }

            return StreamDestination.CreateWithStreamKey(session.Id, provider, request.DisplayName, ingestUrl,
                secretProtector.Protect(request.StreamKey!), userId, now);
        }

        if (!descriptor.SupportsLinkedAccount)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"{descriptor.DisplayName} destinations cannot be configured with a linked account.");
        }

        var account = await db.ProviderAccounts
            .FirstOrDefaultAsync(a => a.Id == request.ProviderAccountId, cancellationToken)
            ?? throw new DomainException(ErrorCodes.ProviderAccountUnavailable, "That linked account no longer exists.");

        // Tenancy: an account from another workspace must be indistinguishable from one that does
        // not exist, or this endpoint becomes a way to enumerate other tenants linked channels.
        if (account.WorkspaceId != session.WorkspaceId)
        {
            throw new DomainException(ErrorCodes.ProviderAccountUnavailable, "That linked account no longer exists.");
        }

        if (account.Provider != provider)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "The linked account belongs to a different platform.");
        }

        if (account.Status != ProviderAccountStatus.Connected)
        {
            throw new DomainException(ErrorCodes.ProviderAccountUnavailable,
                $"The {descriptor.DisplayName} account needs to be connected again before it can be used.");
        }

        return StreamDestination.CreateWithLinkedAccount(session.Id, provider, request.DisplayName, account.Id,
            userId, now);
    }

    private string DecryptStreamKey(StreamDestination destination)
    {
        if (destination.StreamKeyCipher is null)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "This destination has no stored stream key.");
        }

        return secretProtector.Unprotect(destination.StreamKeyCipher);
    }

    private static DestinationProvider ParseProvider(string? value)
    {
        if (!Enum.TryParse<DestinationProvider>(value, ignoreCase: true, out var provider))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown destination provider '{value}'.");
        }

        return provider;
    }

    private async Task<LiveSession> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.LiveSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
        ?? throw new DomainException(ErrorCodes.SessionNotFound, "Live session not found.");

    private async Task<(LiveSession Session, StreamDestination Destination)> LoadForReadAsync(Guid sessionId,
        Guid destinationId, Guid userId, CancellationToken cancellationToken) =>
        await LoadAsync(sessionId, destinationId, userId, WorkspacePermission.LiveSessionView, cancellationToken);

    private async Task<(LiveSession Session, StreamDestination Destination)> LoadForWriteAsync(Guid sessionId,
        Guid destinationId, Guid userId, CancellationToken cancellationToken) =>
        await LoadAsync(sessionId, destinationId, userId, WorkspacePermission.DestinationManage, cancellationToken);

    private async Task<(LiveSession Session, StreamDestination Destination)> LoadAsync(Guid sessionId,
        Guid destinationId, Guid userId, WorkspacePermission permission, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        await authorization.EnsureAllowedAsync(session, userId, permission, cancellationToken);

        var destination = await db.StreamDestinations
            .Include(d => d.ProviderAccount)
            .FirstOrDefaultAsync(d => d.Id == destinationId, cancellationToken)
            ?? throw new DomainException(ErrorCodes.SessionNotFound, "Destination not found.");

        // Checking the parent rather than trusting the route: without this, a destination id from
        // one session could be operated on through another session the caller does have access to.
        if (destination.LiveSessionId != sessionId)
        {
            throw new DomainException(ErrorCodes.SessionNotFound, "Destination not found.");
        }

        return (session, destination);
    }
}

using LiveStream.Domain.Distribution;

namespace LiveStream.Application.Distribution.Contracts;

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Adds a destination to a session.
///
/// Exactly one credential source must be supplied: either <see cref="IngestUrl"/> plus
/// <see cref="StreamKey"/>, or <see cref="ProviderAccountId"/>. The service rejects both and
/// neither, rather than guessing.
/// </summary>
public sealed record CreateDestinationRequest(
    string Provider,
    string DisplayName,
    string? IngestUrl,
    string? StreamKey,
    Guid? ProviderAccountId);

/// <summary>
/// Updates a destination. A null <see cref="StreamKey"/> leaves the stored key untouched, which is
/// what lets the UI render the form without ever holding the current secret.
/// </summary>
public sealed record UpdateDestinationRequest(
    string? DisplayName,
    string? IngestUrl,
    string? StreamKey,
    bool? Enabled);

// ---------------------------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A destination as seen by the operator.
///
/// There is deliberately no stream key field, and no field that could be assembled back into one.
/// implementation/phase-2: "Credentials never appear in frontend responses." <see cref="IngestUrl"/>
/// is safe — it is the platform public endpoint — and is returned with any query string stripped.
/// </summary>
public sealed record DestinationResponse(
    Guid Id,
    Guid LiveSessionId,
    string Provider,
    string DisplayName,
    string CredentialMode,
    string? IngestUrl,
    bool HasStreamKey,
    Guid? ProviderAccountId,
    string? ProviderAccountName,
    bool Enabled,
    string Status,
    string? LastErrorCode,
    string? LastErrorMessage,
    string? WatchUrl,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    long BytesSent,
    int? UptimeSeconds,
    IReadOnlyList<string> AllowedTransitions,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>Body of the <c>destinationStateChanged</c> realtime event (docs/09, MASTER_BLUEPRINT.md §29).</summary>
public sealed record DestinationStatusResponse(
    Guid Id,
    Guid LiveSessionId,
    string Provider,
    string DisplayName,
    string Status,
    bool Enabled,
    string? LastErrorCode,
    string? LastErrorMessage,
    string? WatchUrl,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    int? UptimeSeconds,
    long BytesSent,
    DateTimeOffset ObservedAt);

public sealed record DestinationEventResponse(
    Guid Id,
    string Type,
    string? FromStatus,
    string? ToStatus,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CreatedAt);

/// <summary>
/// A linked external account. Carries no tokens: the encrypted values never leave the server, and
/// there is no endpoint that returns them.
/// </summary>
public sealed record ProviderAccountResponse(
    Guid Id,
    Guid WorkspaceId,
    string Provider,
    string ExternalAccountId,
    string DisplayName,
    string Status,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>What the UI needs to render the "add destination" form for each platform.</summary>
public sealed record ProviderDescriptorResponse(
    string Provider,
    string DisplayName,
    bool SupportsStreamKey,
    bool SupportsLinkedAccount,
    bool LinkedAccountConfigured,
    string? DefaultIngestUrl,
    string StreamKeyHelp,
    string? HelpUrl);

/// <summary>Where to send the operator to grant consent, and the state value to echo back.</summary>
public sealed record ProviderAuthorizationResponse(string AuthorizationUrl, string State, DateTimeOffset ExpiresAt);

public sealed record CompleteProviderAuthorizationRequest(string Code, string State);

// ---------------------------------------------------------------------------------------------
// Mapping
// ---------------------------------------------------------------------------------------------

public static class DestinationMapper
{
    public static DestinationResponse ToResponse(StreamDestination destination, DateTimeOffset now) => new(
        destination.Id,
        destination.LiveSessionId,
        destination.Provider.ToString(),
        destination.DisplayName,
        destination.CredentialMode.ToString(),
        StripQuery(destination.IngestUrl),
        destination.StreamKeyCipher is not null,
        destination.ProviderAccountId,
        destination.ProviderAccount?.DisplayName,
        destination.Enabled,
        destination.Status.ToString(),
        destination.LastErrorCode,
        destination.LastErrorMessage,
        destination.WatchUrl,
        destination.AttemptCount,
        destination.NextRetryAt,
        destination.StartedAt,
        destination.StoppedAt,
        destination.BytesSent,
        UptimeSeconds(destination, now),
        DestinationStateMachine.AllowedTargets(destination.Status).Select(s => s.ToString()).ToList(),
        destination.UpdatedAt,
        destination.Version);

    public static DestinationStatusResponse ToStatusResponse(StreamDestination destination, DateTimeOffset now) => new(
        destination.Id,
        destination.LiveSessionId,
        destination.Provider.ToString(),
        destination.DisplayName,
        destination.Status.ToString(),
        destination.Enabled,
        destination.LastErrorCode,
        destination.LastErrorMessage,
        destination.WatchUrl,
        destination.AttemptCount,
        destination.NextRetryAt,
        UptimeSeconds(destination, now),
        destination.BytesSent,
        now);

    public static DestinationEventResponse ToResponse(DestinationEvent destinationEvent) => new(
        destinationEvent.Id,
        destinationEvent.Type.ToString(),
        destinationEvent.FromStatus?.ToString(),
        destinationEvent.ToStatus?.ToString(),
        destinationEvent.ErrorCode,
        destinationEvent.Detail,
        destinationEvent.CreatedAt);

    public static ProviderAccountResponse ToResponse(ProviderAccount account) => new(
        account.Id,
        account.WorkspaceId,
        account.Provider.ToString(),
        account.ExternalAccountId,
        account.DisplayName,
        account.Status.ToString(),
        account.LastErrorCode,
        account.LastErrorMessage,
        account.AccessTokenExpiresAt,
        account.CreatedAt,
        account.UpdatedAt);

    private static int? UptimeSeconds(StreamDestination destination, DateTimeOffset now)
    {
        if (destination.StartedAt is not { } startedAt)
        {
            return null;
        }

        var until = destination.StoppedAt ?? now;
        var seconds = (int)(until - startedAt).TotalSeconds;
        return seconds < 0 ? 0 : seconds;
    }

    /// <summary>
    /// Removes any query string before an ingest URL is returned. Some platforms embed the key as a
    /// query parameter, so returning the URL verbatim would leak exactly what this contract promises
    /// not to.
    /// </summary>
    private static string? StripQuery(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        var queryStart = url.IndexOf('?');
        return queryStart < 0 ? url : url[..queryStart];
    }
}

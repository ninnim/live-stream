namespace LiveStream.Application.Governance.Contracts;

// ---------------------------------------------------------------------------------------------
// Tenant limits
// ---------------------------------------------------------------------------------------------

/// <summary>
/// What a workspace is allowed. <c>Effective*</c> is what is actually enforced — the tightest of
/// the deployment cap, the plan, and the workspace's own override — while <c>PlanMax*</c> is the
/// ceiling the plan permits, so the UI can show how much headroom an admin has.
/// </summary>
public sealed record WorkspaceLimitsResponse(
    Guid WorkspaceId,
    string Plan,
    int EffectiveMaxConcurrentSessions,
    int EffectiveMaxDestinationsPerSession,
    int EffectiveMaxSourcesPerSession,
    int EffectiveRecordingRetentionDays,
    int PlanMaxConcurrentSessions,
    int PlanMaxDestinationsPerSession,
    int PlanMaxSourcesPerSession,
    int PlanRecordingRetentionDays,
    int? MaxConcurrentSessionsOverride,
    int? MaxDestinationsPerSessionOverride,
    int? MaxSourcesPerSessionOverride,
    int? RecordingRetentionDaysOverride,
    string? ResidencyRegion,
    string? DeploymentRegion,
    bool SingleSignOnAllowed,
    bool DataResidencyAllowed,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Replaces every override at once. <c>null</c> means "no override": PUT semantics, because a
/// partial update gives no way to say "go back to what the plan allows".
/// </summary>
public sealed record UpdateWorkspaceLimitsRequest(
    int? MaxConcurrentSessions,
    int? MaxDestinationsPerSession,
    int? MaxSourcesPerSession,
    int? RecordingRetentionDays,
    string? ResidencyRegion);

/// <summary>Operator-only: moves a workspace between plans.</summary>
public sealed record ChangePlanRequest(string Plan);

// ---------------------------------------------------------------------------------------------
// Usage and cost
// ---------------------------------------------------------------------------------------------

/// <summary>Headroom right now: what is running against what is allowed.</summary>
public sealed record WorkspaceCapacityResponse(
    int OpenSessions,
    int BroadcastingSessions,
    int MaxConcurrentSessions,
    int MaxDestinationsPerSession,
    int MaxSourcesPerSession,
    int RecordingRetentionDays,
    string? ResidencyRegion);

/// <summary>Measured usage over the reported window. Every figure comes from stored rows.</summary>
public sealed record WorkspaceUsageTotals(
    int Sessions,
    double StreamingHours,
    double RelayHours,
    double IngestGb,
    double RelayEgressGb,
    double StoredRecordingGb,
    int RecordingsStored,
    int AiJobs);

/// <summary>
/// One line of the cost estimate. <see cref="Amount"/> is null when the deployment has not
/// configured a rate for it, which reads differently from a configured rate that came to zero.
/// </summary>
public sealed record CostLine(string Key, string Label, double Quantity, string Unit, decimal? Rate, decimal? Amount);

public sealed record WorkspaceCostResponse(
    string Currency,
    bool RatesConfigured,
    IReadOnlyList<CostLine> Lines,
    decimal? EstimatedTotal,
    IReadOnlyList<string> NotMetered);

public sealed record WorkspaceUsageResponse(
    Guid WorkspaceId,
    string Plan,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    WorkspaceCapacityResponse Capacity,
    WorkspaceUsageTotals Usage,
    WorkspaceCostResponse Cost);

// ---------------------------------------------------------------------------------------------
// Platform capacity (operator)
// ---------------------------------------------------------------------------------------------

public sealed record LeaseStatusResponse(string Name, string OwnerId, DateTimeOffset ExpiresAt, long FencingToken,
    bool Held);

public sealed record AiQueueStatusResponse(int Queued, int Running, double? OldestQueuedAgeSeconds);

public sealed record RecordingCapacityResponse(int Ready, double StoredGb, int ExpiringWithin24Hours, int PendingDeletion);

public sealed record PlatformCapacityResponse(
    string InstanceId,
    string Role,
    string? Region,
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, int> SessionsByStatus,
    int BroadcastingSessions,
    int ConnectedSources,
    int LiveDestinations,
    int Workspaces,
    int WorkspacesBroadcasting,
    AiQueueStatusResponse Ai,
    RecordingCapacityResponse Recordings,
    IReadOnlyList<LeaseStatusResponse> Leases);

// ---------------------------------------------------------------------------------------------
// Single sign-on
// ---------------------------------------------------------------------------------------------

public sealed record SsoDomainResponse(string Domain, bool Verified, DateTimeOffset? VerifiedAt);

/// <summary>
/// The connection as an admin sees it. There is no field for the client secret: it goes in and is
/// never read back out, exactly like a destination stream key.
/// </summary>
public sealed record SsoConnectionResponse(
    Guid WorkspaceId,
    string Protocol,
    string Issuer,
    string ClientId,
    bool Enabled,
    bool JitProvisioning,
    string DefaultRole,
    bool Usable,
    IReadOnlyList<SsoDomainResponse> Domains,
    string RedirectUri,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset UpdatedAt);

public sealed record UpsertSsoConnectionRequest(
    string Issuer,
    string ClientId,
    string? ClientSecret,
    bool Enabled,
    bool JitProvisioning,
    string DefaultRole,
    IReadOnlyList<string> Domains);

/// <summary>What the sign-in page needs to decide whether to offer single sign-on for an address.</summary>
public sealed record SsoDiscoveryResponse(bool Available, string? WorkspaceName);

public sealed record SsoStartResponse(string AuthorizationUrl, string State);

// ---------------------------------------------------------------------------------------------
// Data export and erasure
// ---------------------------------------------------------------------------------------------

public sealed record WorkspaceExportResponse(
    DateTimeOffset GeneratedAt,
    Guid WorkspaceId,
    string WorkspaceName,
    string Plan,
    bool Truncated,
    IReadOnlyList<ExportedMember> Members,
    IReadOnlyList<ExportedSession> Sessions);

public sealed record ExportedMember(string Email, string DisplayName, string Role, DateTimeOffset JoinedAt);

public sealed record ExportedSession(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    string Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    IReadOnlyList<ExportedEvent> Events,
    IReadOnlyList<ExportedRecording> Recordings,
    IReadOnlyList<ExportedDestination> Destinations);

public sealed record ExportedEvent(DateTimeOffset At, string Type, string? Detail, string? ErrorCode);

public sealed record ExportedRecording(Guid Id, string Status, int? DurationSeconds, long? SizeBytes,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? DeletedAt);

public sealed record ExportedDestination(string Provider, string DisplayName, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, long BytesSent);

/// <summary>What erasure removed, so the operator has a receipt for it.</summary>
public sealed record WorkspaceErasureResponse(
    Guid WorkspaceId,
    int SessionsDeleted,
    int RecordingsDeleted,
    long BytesFreed,
    DateTimeOffset CompletedAt);

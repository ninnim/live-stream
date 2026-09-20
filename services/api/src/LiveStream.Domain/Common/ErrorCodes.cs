namespace LiveStream.Domain.Common;

/// <summary>
/// Stable internal error codes (MASTER_BLUEPRINT.md §34). These strings are part of the
/// public API contract: clients may branch on them, so they must not change meaning.
/// </summary>
public static class ErrorCodes
{
    public const string SessionNotFound = "LIVE_001_SESSION_NOT_FOUND";
    public const string SessionNotReady = "LIVE_002_SESSION_NOT_READY";
    public const string StreamStartFailed = "LIVE_003_STREAM_START_FAILED";
    public const string DestinationAuthFailed = "LIVE_004_DESTINATION_AUTH_FAILED";
    public const string SourceDisconnected = "LIVE_005_SOURCE_DISCONNECTED";
    public const string NetworkDegraded = "LIVE_006_NETWORK_DEGRADED";
    public const string RecordingFailed = "LIVE_007_RECORDING_FAILED";
    public const string PermissionDenied = "LIVE_008_PERMISSION_DENIED";
    public const string InvalidStateTransition = "LIVE_009_INVALID_STATE_TRANSITION";
    public const string ValidationFailed = "LIVE_010_VALIDATION_FAILED";
    public const string CredentialExpired = "LIVE_011_CREDENTIAL_EXPIRED";
    public const string MediaGatewayUnavailable = "LIVE_012_MEDIA_GATEWAY_UNAVAILABLE";
    public const string ConcurrencyConflict = "LIVE_013_CONCURRENCY_CONFLICT";
    public const string RateLimited = "LIVE_014_RATE_LIMITED";
    public const string AuthenticationFailed = "LIVE_015_AUTHENTICATION_FAILED";

    // ---------------------------------------------------------------------------------------
    // Phase 2 — multi-platform distribution.
    //
    // Provider APIs return wildly inconsistent errors; these codes are the normalized vocabulary
    // the rest of the system branches on (implementation/phase-2: "Provider-specific errors are
    // normalized into stable internal error codes").
    // ---------------------------------------------------------------------------------------

    /// <summary>The platform refused the connection or the stream key. Not retryable without operator action.</summary>
    public const string DestinationRejected = "LIVE_016_DESTINATION_REJECTED";

    /// <summary>The platform was unreachable or dropped the connection. Retryable.</summary>
    public const string DestinationUnavailable = "LIVE_017_DESTINATION_UNAVAILABLE";

    /// <summary>The relay service that pushes to external platforms could not be reached.</summary>
    public const string RelayUnavailable = "LIVE_018_RELAY_UNAVAILABLE";

    /// <summary>The linked account is missing, revoked, or needs fresh consent.</summary>
    public const string ProviderAccountUnavailable = "LIVE_019_PROVIDER_ACCOUNT_UNAVAILABLE";

    /// <summary>The provider API returned an error we could not classify more precisely.</summary>
    public const string ProviderApiError = "LIVE_020_PROVIDER_API_ERROR";

    /// <summary>The provider rejected the request for quota or eligibility reasons.</summary>
    public const string ProviderQuotaExceeded = "LIVE_021_PROVIDER_QUOTA_EXCEEDED";

    /// <summary>The session already has as many destinations as configuration permits.</summary>
    public const string DestinationLimitReached = "LIVE_022_DESTINATION_LIMIT_REACHED";

    /// <summary>A stored secret could not be encrypted or decrypted; usually a key rotation problem.</summary>
    public const string SecretProtectionFailed = "LIVE_023_SECRET_PROTECTION_FAILED";

    // ---------------------------------------------------------------------------------------
    // Phase 4 — multi-device and collaboration.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The pairing code is unknown, expired, or already redeemed.
    ///
    /// Deliberately one code for all three: distinguishing them would tell someone guessing codes
    /// which of their guesses had ever been real.
    /// </summary>
    public const string PairingCodeInvalid = "LIVE_024_PAIRING_CODE_INVALID";

    /// <summary>The requested source does not exist, or is not part of the session named in the route.</summary>
    public const string SourceNotFound = "LIVE_025_SOURCE_NOT_FOUND";

    /// <summary>The device token is expired, or the device has been revoked.</summary>
    public const string DeviceNotAuthorized = "LIVE_026_DEVICE_NOT_AUTHORIZED";

    /// <summary>The session already has as many sources as configuration permits.</summary>
    public const string SourceLimitReached = "LIVE_027_SOURCE_LIMIT_REACHED";

    // -----------------------------------------------------------------------------------------
    // Phase 5 — Professional Live Studio
    // -----------------------------------------------------------------------------------------

    /// <summary>No such scene in this session, or the scene belongs to a different one.</summary>
    public const string SceneNotFound = "LIVE_028_SCENE_NOT_FOUND";

    // -----------------------------------------------------------------------------------------
    // Phase 6 — AI Live Operations
    // -----------------------------------------------------------------------------------------

    /// <summary>No AI provider is configured, so the work could not even be queued.</summary>
    public const string AiUnavailable = "LIVE_029_AI_UNAVAILABLE";

    /// <summary>The provider exists but this particular feature is switched off for the deployment.</summary>
    public const string AiFeatureDisabled = "LIVE_030_AI_FEATURE_DISABLED";

    /// <summary>No such AI job in this session, or the job belongs to a different one.</summary>
    public const string AiJobNotFound = "LIVE_031_AI_JOB_NOT_FOUND";

    // -----------------------------------------------------------------------------------------
    // Phase 7 — Scale, Security & Globalization
    // -----------------------------------------------------------------------------------------

    /// <summary>No such workspace, or the caller is not a member of it.</summary>
    public const string WorkspaceNotFound = "LIVE_032_WORKSPACE_NOT_FOUND";

    /// <summary>
    /// The workspace's plan does not allow this. Distinct from <see cref="RateLimited"/>: a rate
    /// limit clears by waiting, a plan limit clears only by changing the plan.
    /// </summary>
    public const string PlanLimitReached = "LIVE_033_PLAN_LIMIT_REACHED";

    /// <summary>
    /// The workspace's data residency region is not the one this deployment serves. The request
    /// belongs in another region and must not be silently served here.
    /// </summary>
    public const string RegionNotAvailable = "LIVE_034_REGION_NOT_AVAILABLE";

    /// <summary>Single sign-on is not set up for this workspace, or has been switched off.</summary>
    public const string SsoNotConfigured = "LIVE_035_SSO_NOT_CONFIGURED";

    /// <summary>
    /// The single sign-on exchange failed. One code for every cause, so a probe cannot learn which
    /// step it got past.
    /// </summary>
    public const string SsoFailed = "LIVE_036_SSO_FAILED";

    /// <summary>The recording is gone: its workspace retention window elapsed and it was deleted.</summary>
    public const string RecordingExpired = "LIVE_037_RECORDING_EXPIRED";

    /// <summary>
    /// A password reset link is expired, already used, or was never valid.
    ///
    /// One code for all three, deliberately. Telling the difference would let somebody holding a
    /// stolen link learn whether it had been used — which is exactly the thing they would want to
    /// know, and exactly what the person it was stolen from needs them not to.
    /// </summary>
    public const string ResetTokenInvalid = "LIVE_038_RESET_TOKEN_INVALID";
}

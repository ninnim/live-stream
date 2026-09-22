/**
 * Contracts published by the control-plane API.
 *
 * These mirror `services/api/src/LiveStream.Application/Sessions/Contracts` and
 * `.../Auth/AuthContracts.cs`. Keep them in step: the API is the source of truth, and the studio
 * must never infer session state locally.
 */

export type LiveSessionStatus =
  | "DRAFT"
  | "PREPARING"
  | "READY"
  | "STARTING"
  | "LIVE"
  | "DEGRADED"
  | "RECONNECTING"
  | "STOPPING"
  | "ENDED"
  | "FAILED";

export type StreamHealthStatus = "UNKNOWN" | "GOOD" | "FAIR" | "POOR";

export type LiveSessionVisibility = "PRIVATE" | "UNLISTED" | "PUBLIC";

export interface LiveSessionHealth {
  status: StreamHealthStatus;
  ingestConnected: boolean;
  bitrateKbps: number | null;
  videoFps: number | null;
  viewerCount: number;
  reconnectCount: number;
  lastIngestAt: string | null;
  recoveryWindowSecondsRemaining: number | null;
  observedAt: string | null;
  lastErrorCode: string | null;
}

export interface Playback {
  hlsUrl: string;
  webRtcUrl: string;
  isLive: boolean;
}

export interface LiveSession {
  id: string;
  workspaceId: string;
  title: string;
  description: string | null;
  status: LiveSessionStatus;
  visibility: LiveSessionVisibility;
  recordingEnabled: boolean;
  startedAt: string | null;
  endedAt: string | null;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  createdAt: string;
  updatedAt: string;
  version: number;
  health: LiveSessionHealth;
  playback: Playback | null;
}

export interface LiveSessionStatusPayload {
  id: string;
  status: LiveSessionStatus;
  allowedTransitions: LiveSessionStatus[];
  isBroadcasting: boolean;
  isTerminal: boolean;
  startedAt: string | null;
  endedAt: string | null;
  durationSeconds: number | null;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  version: number;
  observedAt: string;
}

/** An ICE server supplied by the API. Mirrors the browser's `RTCIceServer` shape. */
export interface IceServer {
  urls: string[];
  username: string | null;
  credential: string | null;
}

/**
 * A short-lived broadcaster credential. The token is held in memory only for the lifetime of one
 * connection attempt; it is never written to storage.
 */
export interface IngestCredential {
  protocol: string;
  ingestUrl: string;
  token: string;
  expiresAt: string;
  expiresInSeconds: number;
  /** Supplied by the server so no infrastructure address is compiled into the bundle. */
  iceServers: IceServer[];
}

/**
 * What an external encoder needs to publish into a session — a phone streaming a game, OBS, a
 * capture card (ADR 0022).
 *
 * `streamKey` is returned exactly once, when it is issued, and is never retrievable afterwards:
 * the server stores only a hash. Asking again rotates it, which is also how a key pasted into the
 * wrong window is made harmless.
 */
export interface StreamKey {
  protocol: string;
  /** The server half, for encoders with two fields. */
  serverUrl: string;
  /** The key half. Begins with the session path, so server + "/" + key is the full URL. */
  streamKey: string;
  /** Pre-joined, for encoders and command lines that take one URL. */
  fullUrl: string;
  /** Null unless the deployment also offers SRT, which survives a lossy mobile uplink better. */
  srtUrl: string | null;
  expiresAt: string;
  expiresInSeconds: number;
}

export interface Recording {
  id: string;
  status: "PENDING" | "RECORDING" | "FINALIZING" | "READY" | "FAILED";
  storageKey: string;
  mediaFormat: string;
  durationSeconds: number | null;
  sizeBytes: number | null;
  startedAt: string | null;
  endedAt: string | null;
  failureReason: string | null;
}

export interface LiveSessionEvent {
  id: string;
  type: string;
  fromStatus: LiveSessionStatus | null;
  toStatus: LiveSessionStatus | null;
  errorCode: string | null;
  detail: string | null;
  createdAt: string;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface WorkspaceMembership {
  workspaceId: string;
  name: string;
  role: string;
  isOwner: boolean;
}

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  workspaces: WorkspaceMembership[];
}

export interface AuthResult {
  accessToken: string;
  expiresInSeconds: number;
  refreshToken: string;
  user: CurrentUser;
}

// ---------------------------------------------------------------------------------------------
// Multi-platform distribution
// ---------------------------------------------------------------------------------------------

export type DestinationProvider = "CustomRtmp" | "YouTube" | "Facebook" | "TikTok" | "Twitch";

export type DestinationStatus =
  | "Idle"
  | "Preparing"
  | "Connecting"
  | "Live"
  | "Retrying"
  | "Stopping"
  | "Stopped"
  | "Error"
  | "Disabled";

export type DestinationCredentialMode = "StreamKey" | "LinkedAccount";

/**
 * A destination as the studio sees it.
 *
 * There is deliberately no stream key field. `hasStreamKey` is all the UI needs to know: whether
 * one is configured, never what it is.
 */
export interface Destination {
  id: string;
  liveSessionId: string;
  provider: DestinationProvider;
  displayName: string;
  credentialMode: DestinationCredentialMode;
  ingestUrl: string | null;
  hasStreamKey: boolean;
  providerAccountId: string | null;
  providerAccountName: string | null;
  enabled: boolean;
  status: DestinationStatus;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  watchUrl: string | null;
  attemptCount: number;
  nextRetryAt: string | null;
  startedAt: string | null;
  stoppedAt: string | null;
  bytesSent: number;
  uptimeSeconds: number | null;
  allowedTransitions: DestinationStatus[];
  updatedAt: string;
  version: number;
}

/** Body of the `destinationStateChanged` realtime event. */
export interface DestinationStatusPayload {
  id: string;
  liveSessionId: string;
  provider: DestinationProvider;
  displayName: string;
  status: DestinationStatus;
  enabled: boolean;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  watchUrl: string | null;
  attemptCount: number;
  nextRetryAt: string | null;
  uptimeSeconds: number | null;
  bytesSent: number;
  observedAt: string;
}

export interface DestinationEvent {
  id: string;
  type: string;
  fromStatus: DestinationStatus | null;
  toStatus: DestinationStatus | null;
  errorCode: string | null;
  detail: string | null;
  createdAt: string;
}

/** What the UI needs to render the right form for each platform. */
export interface ProviderDescriptor {
  provider: DestinationProvider;
  displayName: string;
  supportsStreamKey: boolean;
  supportsLinkedAccount: boolean;
  linkedAccountConfigured: boolean;
  defaultIngestUrl: string | null;
  streamKeyHelp: string;
  helpUrl: string | null;
}

export interface ProviderAccount {
  id: string;
  workspaceId: string;
  provider: DestinationProvider;
  externalAccountId: string;
  displayName: string;
  status: "Connected" | "NeedsReauthorization" | "Revoked";
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  accessTokenExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

// ---------------------------------------------------------------------------------------------
// Multi-device contribution
// ---------------------------------------------------------------------------------------------

export type SourceRole =
  | "Host"
  | "Camera"
  | "Screen"
  | "Audio"
  | "Moderator"
  | "Operator"
  | "ViewerMonitor";

export type SourceStatus = "Invited" | "Paired" | "Connected" | "Disconnected" | "Revoked";

export type SourcePermission =
  | "PublishMedia"
  | "ViewSession"
  | "SwitchProgram"
  | "ManageDevices"
  | "ModerateChat";

/**
 * A device or participant contributing to a session.
 *
 * Carries no pairing code and no device token: both are returned exactly once, at the moment they
 * are minted, and are stored only as hashes.
 */
export interface SessionSource {
  id: string;
  liveSessionId: string;
  role: SourceRole;
  displayName: string;
  status: SourceStatus;
  isProgram: boolean;
  contributesMedia: boolean;
  ingestConnected: boolean;
  bitrateKbps: number | null;
  bytesReceived: number;
  lastSeenAt: string | null;
  pairedAt: string | null;
  revokedAt: string | null;
  permissions: SourcePermission[];
  allowedTransitions: SourceStatus[];
  updatedAt: string;
  version: number;
}

/** Returned once when a device is invited. The code is never retrievable again. */
export interface SourceInvitation {
  source: SessionSource;
  pairingCode: string;
  joinUrl: string;
  expiresAt: string;
  expiresInSeconds: number;
}

/** Returned once when a device redeems a code. */
export interface DevicePaired {
  deviceToken: string;
  expiresAt: string;
  liveSessionId: string;
  sessionTitle: string;
  source: SessionSource;
}

/** What a paired device is allowed to know about the session it joined. */
export interface DeviceSession {
  liveSessionId: string;
  sessionTitle: string;
  sessionStatus: LiveSessionStatus;
  isBroadcasting: boolean;
  source: SessionSource;
  permissions: SourcePermission[];
}

/** A short-lived way for the control room to watch one source. */
export interface SourcePreview {
  sourceId: string;
  webRtcUrl: string;
  hlsUrl: string;
  readToken: string;
  expiresAt: string;
}

/** Body of the `sourceStateChanged` realtime event. */
export interface SourceStatusPayload {
  id: string;
  liveSessionId: string;
  role: SourceRole;
  displayName: string;
  status: SourceStatus;
  isProgram: boolean;
  ingestConnected: boolean;
  bitrateKbps: number | null;
  lastSeenAt: string | null;
  observedAt: string;
}

/** Client-side transport events reported to the API for diagnostics only. */
export type BroadcasterSignal =
  | "INGEST_CONNECTED"
  | "INGEST_DISCONNECTED"
  | "RECONNECT_ATTEMPT"
  | "RECONNECT_SUCCEEDED"
  | "RECONNECT_FAILED"
  | "DEVICE_ERROR"
  | "CLIENT_ERROR";

// ---------------------------------------------------------------------------------------------
// Studio configuration (Phase 5)
// ---------------------------------------------------------------------------------------------

/** Where a watermark sits in the frame. */
export type LogoPosition = "TopLeft" | "TopRight" | "BottomLeft" | "BottomRight";

/** How the sources in a scene are arranged. Mirrors the studio's own layouts. */
export type SceneLayout = "Solo" | "SideBySide" | "PictureInPicture";

/** The look a session broadcasts with. Every session has one from the outset. */
export interface SessionBranding {
  liveSessionId: string;
  /** A PNG, JPEG or WebP data URI. Same-origin by construction — see the server for why. */
  logoDataUri: string | null;
  logoPosition: LogoPosition;
  logoOpacityPercent: number;
  accentColor: string;
  showLogo: boolean;
  updatedAt: string;
}

export interface UpdateBranding {
  logoDataUri?: string | null;
  /** Absent and null mean different things: leave the logo alone, versus clear it. */
  replaceLogo: boolean;
  logoPosition?: LogoPosition;
  logoOpacityPercent?: number;
  accentColor?: string;
  showLogo?: boolean;
}

/** A prepared shot the control room can recall. */
export interface SessionScene {
  id: string;
  liveSessionId: string;
  name: string;
  layout: SceneLayout;
  primarySourceId: string | null;
  secondarySourceId: string | null;
  lowerThirdTitle: string | null;
  lowerThirdSubtitle: string | null;
  position: number;
  updatedAt: string;
}

export interface SaveScene {
  name: string;
  layout: SceneLayout;
  primarySourceId: string | null;
  secondarySourceId: string | null;
  lowerThirdTitle: string | null;
  lowerThirdSubtitle: string | null;
}

// ---------------------------------------------------------------------------------------------
// AI operations (Phase 6)
// ---------------------------------------------------------------------------------------------

export type AiJobKind = "SessionRecap" | "StreamQualityReview" | "Chapters";

export type AiJobStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";

/**
 * One AI job.
 *
 * Carries its whole lifecycle — attempts, timings, tokens, cost, failure — because an AI feature
 * that cannot be inspected is one nobody can trust or budget for.
 */
export interface AiJob {
  id: string;
  liveSessionId: string;
  kind: AiJobKind;
  status: AiJobStatus;
  attemptCount: number;
  maxAttempts: number;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  nextAttemptAt: string | null;
  summary: string | null;
  resultJson: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  modelId: string | null;
  inputTokens: number;
  outputTokens: number;
  /** Estimated from published list prices. Null when the model's price is unknown. */
  estimatedCostUsd: number | null;
  /** The window of session activity the answer was derived from. */
  sourceRangeStart: string | null;
  sourceRangeEnd: string | null;
  updatedAt: string;
}

export interface AiFeature {
  kind: AiJobKind;
  displayName: string;
  description: string;
  enabled: boolean;
}

/** What this deployment can actually do, so the UI never offers a button that cannot work. */
export interface AiCapabilities {
  configured: boolean;
  features: AiFeature[];
}

// ---------------------------------------------------------------------------------------------
// Phase 7 — scale, security, and governance
// ---------------------------------------------------------------------------------------------

export type WorkspacePlan = "FREE" | "PRO" | "BUSINESS" | "ENTERPRISE";

/**
 * What a workspace is allowed.
 *
 * `effective*` is what is enforced — the tightest of the deployment cap, the plan, and the
 * workspace's own override. `planMax*` is the ceiling the plan permits, which is what tells an
 * admin how much room they have to give away.
 */
export interface WorkspaceLimits {
  workspaceId: string;
  plan: WorkspacePlan;
  effectiveMaxConcurrentSessions: number;
  effectiveMaxDestinationsPerSession: number;
  effectiveMaxSourcesPerSession: number;
  effectiveRecordingRetentionDays: number;
  planMaxConcurrentSessions: number;
  planMaxDestinationsPerSession: number;
  planMaxSourcesPerSession: number;
  planRecordingRetentionDays: number;
  maxConcurrentSessionsOverride: number | null;
  maxDestinationsPerSessionOverride: number | null;
  maxSourcesPerSessionOverride: number | null;
  recordingRetentionDaysOverride: number | null;
  residencyRegion: string | null;
  deploymentRegion: string | null;
  singleSignOnAllowed: boolean;
  dataResidencyAllowed: boolean;
  updatedAt: string;
}

export interface UpdateWorkspaceLimits {
  maxConcurrentSessions: number | null;
  maxDestinationsPerSession: number | null;
  maxSourcesPerSession: number | null;
  recordingRetentionDays: number | null;
  residencyRegion: string | null;
}

export interface WorkspaceCapacity {
  openSessions: number;
  broadcastingSessions: number;
  maxConcurrentSessions: number;
  maxDestinationsPerSession: number;
  maxSourcesPerSession: number;
  recordingRetentionDays: number;
  residencyRegion: string | null;
}

export interface WorkspaceUsageTotals {
  sessions: number;
  streamingHours: number;
  relayHours: number;
  ingestGb: number;
  relayEgressGb: number;
  storedRecordingGb: number;
  recordingsStored: number;
  aiJobs: number;
}

/** `amount` is null when the deployment has configured no rate — which reads differently from zero. */
export interface CostLine {
  key: string;
  label: string;
  quantity: number;
  unit: string;
  rate: number | null;
  amount: number | null;
}

export interface WorkspaceCost {
  currency: string;
  ratesConfigured: boolean;
  lines: CostLine[];
  estimatedTotal: number | null;
  /** Named rather than reported as zero, so nobody budgets against a cost the platform cannot see. */
  notMetered: string[];
}

export interface WorkspaceUsage {
  workspaceId: string;
  plan: WorkspacePlan;
  periodStart: string;
  periodEnd: string;
  capacity: WorkspaceCapacity;
  usage: WorkspaceUsageTotals;
  cost: WorkspaceCost;
}

export interface SsoDomain {
  domain: string;
  verified: boolean;
  verifiedAt: string | null;
}

/** There is no client-secret field: it goes in and is never read back, like a stream key. */
export interface SsoConnection {
  workspaceId: string;
  protocol: string;
  issuer: string;
  clientId: string;
  enabled: boolean;
  jitProvisioning: boolean;
  defaultRole: string;
  usable: boolean;
  domains: SsoDomain[];
  redirectUri: string;
  lastUsedAt: string | null;
  updatedAt: string;
}

export interface UpsertSsoConnection {
  issuer: string;
  clientId: string;
  /** Omitted when unchanged, so editing the other fields cannot blank the stored secret. */
  clientSecret: string | null;
  enabled: boolean;
  jitProvisioning: boolean;
  defaultRole: string;
  domains: string[];
}

export interface SsoDiscovery {
  available: boolean;
  workspaceName: string | null;
}

export interface SsoStart {
  authorizationUrl: string;
  state: string;
}

export interface WorkspaceErasure {
  workspaceId: string;
  sessionsDeleted: number;
  recordingsDeleted: number;
  bytesFreed: number;
  completedAt: string;
}

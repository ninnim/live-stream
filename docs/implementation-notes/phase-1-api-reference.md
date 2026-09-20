# Phase 1 — API and realtime reference

Implemented surface as of Phase 1. Follows the conventions in `docs/09-api-specification.md`.

Base path: `/api/v1`. All timestamps are ISO 8601 UTC.

## Authentication

Bearer token in `Authorization`. Access tokens last 15 minutes; the studio refreshes silently using
a rotating refresh token.

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/auth/register` | anonymous | Creates an account and its first workspace |
| POST | `/auth/login` | anonymous | Returns an access/refresh token pair |
| POST | `/auth/refresh` | anonymous | Rotates the refresh token, issues a new access token |
| POST | `/auth/logout` | required | Revokes the supplied token, or all tokens when omitted |
| GET | `/auth/me` | required | Current user and workspace memberships |

## Live sessions

| Method | Path | Permission | Description |
|---|---|---|---|
| GET | `/live-sessions` | `LiveSessionView` | Paged list, scoped to the caller's workspaces |
| POST | `/live-sessions` | `LiveSessionEdit` | Creates a session in `DRAFT` |
| GET | `/live-sessions/{id}` | `LiveSessionView` | Full session with health and playback URLs |
| PATCH | `/live-sessions/{id}` | `LiveSessionEdit` | Updates details before the broadcast starts |
| GET | `/live-sessions/{id}/status` | `LiveSessionView` | Authoritative state and allowed transitions |
| GET | `/live-sessions/{id}/health` | `LiveSessionView` | Ingest state, bitrate, viewers, recovery window |
| GET | `/live-sessions/{id}/events` | `LiveSessionView` | Audit trail of transitions and media events |
| GET | `/live-sessions/{id}/recordings` | `RecordingView` | Recording metadata |
| POST | `/live-sessions/{id}/prepare` | `LiveSessionStart` | Reserves media resources → `READY` |
| POST | `/live-sessions/{id}/start` | `LiveSessionStart` | Requests `LIVE` |
| POST | `/live-sessions/{id}/stop` | `LiveSessionStop` | Stops, finalizes recording → `ENDED` |
| POST | `/live-sessions/{id}/sources/credentials` | `LiveSessionStart` | Issues a short-lived ingest credential |
| POST | `/live-sessions/{id}/broadcaster-signals` | `LiveSessionStart` | Records a client transport event (diagnostics only) |
| GET | `/live-sessions/{id}/playback` | see below | Playback URLs for a viewer |

`/playback` is anonymous for `PUBLIC` and `UNLISTED` sessions; `PRIVATE` sessions require workspace
membership.

### Idempotency

`prepare`, `start`, and `stop` are safe to retry. Repeating the current state is a no-op rather than
an error, so a dropped response or a double-click cannot corrupt session state.

## Internal endpoints

Called by the media gateway. Not part of the public API and not reachable from outside the internal
network. Authenticated with a shared secret in the `X-Internal-Auth` header or the `s` query
parameter.

| Method | Path | Description |
|---|---|---|
| POST | `/internal/media/auth` | Authorizes a publish or read. `200` allow, `401` deny |
| POST | `/internal/media/events` | Optional hook that triggers an out-of-band reconcile |

## Error responses

RFC 9457 problem details plus two extensions: `errorCode` and `correlationId`.

```json
{
  "status": 409,
  "title": "The session must be prepared before it can go live.",
  "type": "https://docs.livestream.local/errors/LIVE_002_SESSION_NOT_READY",
  "instance": "/api/v1/live-sessions/.../start",
  "errorCode": "LIVE_002_SESSION_NOT_READY",
  "correlationId": "0HN7A..."
}
```

| Code | HTTP | Meaning |
|---|---|---|
| `LIVE_001_SESSION_NOT_FOUND` | 404 | No such session |
| `LIVE_002_SESSION_NOT_READY` | 409 | Wrong state for the requested operation |
| `LIVE_003_STREAM_START_FAILED` | 502 | No media arrived within the start timeout |
| `LIVE_005_SOURCE_DISCONNECTED` | — | Ingest lost; drives `RECONNECTING` and `FAILED` |
| `LIVE_006_NETWORK_DEGRADED` | — | Sustained low bitrate; drives `DEGRADED` |
| `LIVE_007_RECORDING_FAILED` | — | Recording could not be produced; session unaffected |
| `LIVE_008_PERMISSION_DENIED` | 403 | Not a member, or role lacks the permission |
| `LIVE_009_INVALID_STATE_TRANSITION` | 409 | State machine refused the change |
| `LIVE_010_VALIDATION_FAILED` | 400 | Invalid input |
| `LIVE_012_MEDIA_GATEWAY_UNAVAILABLE` | 503 | Media plane unreachable |
| `LIVE_013_CONCURRENCY_CONFLICT` | 409 | Session changed by another device |
| `LIVE_014_RATE_LIMITED` | 429 | Abuse control triggered |
| `LIVE_015_AUTHENTICATION_FAILED` | 401 | Sign-in failed or token invalid |

Codes without an HTTP status appear on session events and health, not as request failures.

## Realtime — hub `/hubs/live`

The access token is passed as an `access_token` query parameter, because a browser cannot set headers
on a WebSocket handshake. Joining a session group is authorized like any other read.

**Client to server**

| Method | Description |
|---|---|
| `JoinSession(sessionId)` | Subscribes after an access check; returns the current status |
| `LeaveSession(sessionId)` | Unsubscribes |
| `Heartbeat()` | Returns server time |

**Server to client**

| Event | Payload | Fired when |
|---|---|---|
| `sessionStateChanged` | `LiveSessionStatusPayload` | Any state transition |
| `healthUpdated` | `(sessionId, health)` | Health status, ingest, reconnect count, or bitrate changes materially |
| `viewerCountUpdated` | `(sessionId, count)` | Viewer count changes |
| `recordingStateChanged` | `(sessionId, recording)` | Recording lifecycle changes |
| `errorRaised` | `(sessionId, { errorCode, message })` | An operational error the studio should show |

Realtime is a latency optimisation, not the source of truth. The studio also polls REST — quickly
while the hub is down, slowly for reconciliation while it is up — so a silently dead socket cannot
leave the studio showing stale state during a broadcast.

## Rate limits

Partitioned by authenticated user, falling back to remote IP. Configurable under `RateLimits`.

| Policy | Default | Applies to |
|---|---|---|
| `AuthenticationPerMinute` | 10 | `/auth/*` |
| `SessionCreationPerMinute` | 20 | `POST /live-sessions` |
| `CredentialIssuancePerMinute` | 60 | Credential issuance — sized so reconnects are never throttled |

Separately, `LiveSessions:MaxConcurrentSessionsPerWorkspace` (default 3) bounds how many non-terminal
sessions a workspace may hold open.

## Configuration

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Postgres` | — | Database connection |
| `Database:MigrateOnStartup` | `false` | Apply migrations at startup (single-instance only) |
| `Jwt:SigningKey` | — | **Secret.** Required, minimum 32 characters |
| `Jwt:AccessTokenLifetimeSeconds` | `900` | Access token lifetime |
| `InternalApi:SharedSecret` | — | **Secret.** Required, minimum 16 characters |
| `Media:MediaMtx:ControlApiUrl` | `http://localhost:9997` | Gateway control API — internal only |
| `Media:MediaMtx:PublicWebRtcUrl` | `http://localhost:8889` | Browser-facing WHIP/WHEP origin |
| `Media:MediaMtx:PublicHlsUrl` | `http://localhost:8888` | Browser-facing HLS origin |
| `Recording:RootPath` | `./recordings` | Shared recording volume |
| `LiveSessions:IngestCredentialLifetimeSeconds` | `300` | Broadcaster credential lifetime |
| `LiveSessions:RecoveryWindowSeconds` | `120` | How long `RECONNECTING` is held before failing |
| `LiveSessions:StartIngestTimeoutSeconds` | `60` | How long `STARTING` waits for media |
| `LiveSessions:HealthPollIntervalSeconds` | `3` | Reconciliation interval |
| `LiveSessions:HealthyBitrateKbps` | `1200` | At or above → `GOOD` |
| `LiveSessions:PoorBitrateKbps` | `400` | Below → `POOR`, session marked `DEGRADED` |
| `Cors:AllowedOrigins` | `["http://localhost:3000"]` | Studio origins |
| `OpenTelemetry:OtlpEndpoint` | empty | OTLP exporter; disabled when empty |

Options are validated at startup, so a misconfigured deployment fails immediately rather than
misbehaving mid-broadcast.

Studio configuration is a single variable, `NEXT_PUBLIC_API_BASE_URL`. It is compiled into the
browser bundle, so no secret may ever be passed through it.

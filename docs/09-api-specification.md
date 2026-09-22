# 09 — API Specification

## REST resources

- `/api/v1/auth/*`
- `/api/v1/workspaces/*`
- `/api/v1/live-sessions/*`
- `/api/v1/live-sessions/{id}/sources`
- `/api/v1/live-sessions/{id}/destinations`
- `/api/v1/live-sessions/{id}/recordings`
- `/api/v1/live-sessions/{id}/analytics`
- `/api/v1/devices/*`

## Core operations

### Create session
`POST /api/v1/live-sessions`

Request:
```json
{
  "title": "My Live",
  "visibility": "public",
  "recordingEnabled": true
}
```

### Start session
`POST /api/v1/live-sessions/{id}/start`

Server validates ownership, session state, source readiness, and permissions.

### Stop session
`POST /api/v1/live-sessions/{id}/stop`

Server transitions state and finalizes asynchronous recording work.

### Issue source credential
`POST /api/v1/live-sessions/{id}/sources/credentials`

Returns short-lived, scoped information only.

### Issue or rotate an encoder stream key
`POST /api/v1/live-sessions/{id}/sources/stream-key`

For external encoders — a phone game capture app, OBS, a capture card — which cannot run the
browser's per-connection credential handshake. Returns the server URL, the key, and a pre-joined
URL, plus an SRT URL where SRT is enabled.

Longer-lived than a browser credential and reusable across reconnects, which is why it is also
rotatable: issuing a key revokes every previous one for the session immediately. The plaintext is
returned exactly once and is never retrievable — only a hash is stored. Returns 400 where the
deployment does not enable external ingest, and 409 once the session has ended.

See `docs/decisions/0022-external-encoder-ingest.md`.

### Revoke encoder stream keys
`DELETE /api/v1/live-sessions/{id}/sources/stream-key`

Revokes the session's encoder keys only; browser broadcasting is unaffected.

## SignalR events

Hub: `/hubs/live`

Client-to-server:
- joinSession
- leaveSession
- heartbeat
- requestControl
- releaseControl

Server-to-client:
- sessionStateChanged
- sourceStateChanged
- destinationStateChanged
- healthUpdated
- viewerCountUpdated
- recordingStateChanged
- errorRaised

## API rules

- All write operations authenticated and authorized.
- Use idempotency keys for retry-sensitive actions.
- Return stable error codes.
- Never expose provider secrets.
- Log correlation IDs.

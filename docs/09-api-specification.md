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

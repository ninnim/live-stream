# Phase 1 — Native Web Broadcasting

## Objective

Deliver the smallest real version of the platform that can broadcast directly from the web app without OBS.

## Scope

### Backend
- Authentication integration compatible with the existing repository.
- Live session CRUD.
- Live session state machine.
- Short-lived source credential issuance.
- SignalR live session hub.
- Basic stream health state model.
- Recording job model.

### Frontend
- Create Live page.
- Live Studio page.
- Camera selector.
- Microphone selector.
- Local preview.
- Start/stop flow.
- Live/reconnecting/error states.
- Basic session timer.
- Basic health indicators.

### Media

Implement the simplest production-sensible native web contribution path supported by the chosen media infrastructure. Keep provider/media implementation behind interfaces. Do not hardwire UI to a specific media vendor.

## Explicitly excluded

- external platform publishing
- mobile apps
- AI
- advanced scenes
- multi-camera composition
- public social feed

## Acceptance criteria

1. Authenticated user creates a live session.
2. User grants browser camera/mic permission.
3. User sees a local preview.
4. User starts a session.
5. Backend confirms LIVE state.
6. A viewer client can play the stream.
7. Temporary network/source interruption moves the session to RECONNECTING when recoverable.
8. Stop completes cleanly.
9. Recording metadata is finalized when recording is enabled.
10. Refreshing the control room does not corrupt session state.

## Implementation order

1. Inspect repository.
2. Establish domain and state machine.
3. Add migrations/entities.
4. Implement session APIs.
5. Implement SignalR events.
6. Implement media gateway integration.
7. Implement web broadcaster.
8. Implement playback.
9. Implement recording finalization.
10. Add tests.
11. Add structured telemetry.
12. Update docs.

## Required final report from coding agent

- files changed
- migrations added
- API endpoints
- realtime events
- environment variables
- how to run locally
- tests executed and results
- known limitations
- rollback notes

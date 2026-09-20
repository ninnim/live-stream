# 14 — Testing Strategy

## Unit tests

Cover:
- session state transitions
- authorization rules
- destination adapter behavior
- reconnect/backoff calculations
- credential expiration
- idempotency

## Integration tests

- API + database
- API + Redis
- SignalR session events
- media orchestrator callbacks
- recording finalization

## End-to-end tests

Critical journey:
1. Create session.
2. Join broadcaster.
3. Start stream.
4. Receive server LIVE state.
5. Verify viewer playback.
6. Stop stream.
7. Verify recording metadata.

## Reliability tests

Simulate:
- temporary network loss
- source disconnect
- destination failure
- API restart
- media worker restart
- duplicate callbacks
- expired credentials

## Performance tests

Measure:
- concurrent session creation
- concurrent live-state updates
- SignalR connection count
- media worker utilization
- recording upload throughput

## Definition

A feature is not complete until automated tests cover the core state and failure paths introduced by the change.

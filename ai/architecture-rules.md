# AI Architecture Rules

## Non-negotiable decisions

- Native broadcasting is the primary workflow.
- OBS is optional and must never be a required dependency.
- Live Session is the central domain aggregate.
- Control plane is independent from media plane.
- Realtime state uses server-authoritative events.
- External destinations use adapters.
- Media provider implementations are isolated.
- Short-lived scoped credentials are used for broadcaster/device ingest.
- Recording belongs to the Live Session but storage is external to the transactional database.

## Anti-patterns

Do not:

- place RTMP/codec details inside UI components;
- put provider-specific logic in generic destination entities;
- trust client-submitted session state;
- use permanent stream keys in browsers;
- couple core session lifetime to one social platform;
- use the relational DB as a raw high-frequency telemetry sink without retention/partitioning strategy;
- make AI required for a basic stream to work.

## Decision hierarchy

1. Safety/security.
2. Correct server-side state.
3. Reliable media path.
4. Backward-compatible APIs.
5. UX quality.
6. Performance optimization.
7. New abstraction convenience.

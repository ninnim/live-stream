# Live Streaming Platform — AI Implementation Documentation Set

This repository is the implementation companion to `MASTER_BLUEPRINT.md`.

## Source of truth

`MASTER_BLUEPRINT.md` defines product intent, architectural direction, scope, and long-term roadmap. When implementation documents appear to conflict, preserve the master blueprint unless a documented architecture decision supersedes it.

## Implementation status

**Phase 1 — Native Web Broadcasting: implemented.** A creator can sign in, create a live session,
grant camera and microphone access, preview, broadcast from the browser without OBS, monitor health,
recover from temporary network loss, and stop cleanly. See
`docs/implementation-notes/phase-1-report.md`.

```text
apps/web/                   Next.js Live Studio and viewer
services/api/               ASP.NET Core control plane
infrastructure/             Docker Compose, MediaMTX config, SQL script
docs/decisions/             Architecture decision records
docs/implementation-notes/  Architecture map, API reference, phase report
docs/troubleshooting/       Local setup and verification
```

Get started: `docs/troubleshooting/phase-1-local-setup.md`.

## Implementation rule

Do not attempt to build the entire platform in one AI coding pass. Implement one phase at a time, with tests, migrations, observability, and documentation completed before moving forward.

## Recommended stack

- Web: Next.js + TypeScript
- Backend: ASP.NET Core / C#
- Realtime control plane: SignalR
- API: REST first; WebSocket/SignalR for live state/events
- Database: PostgreSQL
- Cache / ephemeral coordination: Redis
- Object storage: S3-compatible storage
- Media plane: WebRTC for native browser contribution where appropriate; RTMP/SRT ingest for external encoders; FFmpeg or managed media infrastructure for processing; HLS/LL-HLS/WebRTC for delivery according to latency requirements
- Observability: OpenTelemetry-compatible tracing/metrics/logs
- Deployment: containerized services with horizontal scaling

## Documents

### Core engineering

1. `docs/01-product-requirements.md`
2. `docs/02-system-architecture.md`
3. `docs/03-streaming-engine.md`
4. `docs/04-native-broadcasting.md`
5. `docs/05-multi-device.md`
6. `docs/06-multi-platform-distribution.md`
7. `docs/07-live-studio.md`
8. `docs/08-mobile-app.md`
9. `docs/09-api-specification.md`
10. `docs/10-database-design.md`
11. `docs/11-security.md`
12. `docs/12-observability-and-reliability.md`
13. `docs/13-ai-features.md`
14. `docs/14-testing.md`

### Delivery

- `implementation/phase-1-native-web-broadcasting.md`
- `implementation/phase-2-multi-platform-distribution.md`
- `implementation/phase-3-mobile-broadcasting.md`
- `implementation/phase-4-multi-device-and-collaboration.md`
- `implementation/phase-5-professional-live-studio.md`
- `implementation/phase-6-ai-live-operations.md`
- `implementation/phase-7-scale-security-and-globalization.md`

### AI agent instructions

- `ai/coding-rules.md`
- `ai/architecture-rules.md`
- `ai/implementation-workflow.md`
- `ai/phase-1-agent-prompt.md`
- `ai/definition-of-done.md`

## Architecture decision

The platform is **native-first**. OBS is optional. A normal creator must be able to broadcast directly from your web or mobile application. External encoders are supported as advanced inputs rather than required dependencies.

## Start here

For a new coding-agent session, instruct the agent to read:

1. `MASTER_BLUEPRINT.md`
2. `ai/coding-rules.md`
3. `ai/architecture-rules.md`
4. `ai/implementation-workflow.md`
5. The current phase document only

Then ask it to inspect the actual repository before writing code.

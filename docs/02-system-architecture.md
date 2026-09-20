# 02 — System Architecture

## Architecture principles

1. Separate control plane from media plane.
2. Live Session is the central domain object.
3. Native broadcasting is first-class.
4. External encoder ingest is optional.
5. Realtime state must be event-driven.
6. Media processing must scale independently from business APIs.
7. Do not store high-volume media telemetry directly in the transactional database without an intentional retention strategy.
8. Every asynchronous operation must be idempotent where practical.

## Logical architecture

```text
Web / Mobile / External Encoder
          |
          v
   Edge / API Gateway
          |
   +------+------+
   | Control API |
   +------+------+
          |
  PostgreSQL + Redis
          |
   Session Orchestrator
          |
   +------+-----------------------------+
   |                                    |
Media Ingest / Processing         Realtime Events
   |                                    |
   +----------------+-------------------+
                    |
             Distribution Layer
              /        |        \
          Own App    CDN      External Destinations
```

## Suggested services

- `api`: ASP.NET Core business API.
- `realtime`: SignalR hub(s) for live session control/state.
- `session-orchestrator`: creates/stops sessions, tracks state, coordinates media jobs.
- `media-ingest`: accepts WebRTC/RTMP/SRT contribution depending on implementation.
- `media-worker`: transcoding/packaging/recording.
- `distribution-worker`: external destination publishing.
- `analytics-worker`: aggregates stream metrics.
- `notification-worker`: email/push/web notifications.
- `ai-worker`: future AI jobs.

For early development these can be modular components in fewer deployables. Split services only when operational boundaries justify it.

## State machine

```text
DRAFT
  -> PREPARING
  -> READY
  -> LIVE
  -> RECONNECTING
  -> LIVE
  -> STOPPING
  -> ENDED

Failure paths:
PREPARING -> FAILED
READY -> FAILED
LIVE -> FAILED only for unrecoverable session failure
```

Never allow arbitrary state changes from clients. Server owns the state machine.

## Environments

- local
- development
- staging
- production

Production media credentials must never be used in local development.

## Deployment rule

The first release should be deployable as a small number of containers, but code boundaries must make later horizontal scaling possible.

# Phase 1 — Architecture map

How the native web broadcasting feature fits together, and where to look when changing it.

## Repository layout

```text
live-stream-platform/
├── apps/web/                     Next.js Live Studio and viewer
├── services/api/                 ASP.NET Core control plane (modular monolith)
│   ├── src/LiveStream.Domain/          Entities, enums, state machines — no dependencies
│   ├── src/LiveStream.Application/     Use cases, contracts, abstractions
│   ├── src/LiveStream.Infrastructure/  EF Core, MediaMTX, JWT, recording, providers, crypto
│   ├── src/LiveStream.Api/             Endpoints, SignalR hub, middleware, monitors
│   └── tests/                          Unit, integration, database, shared test doubles
├── services/relay/               Egress relay: FFmpeg per destination (Phase 2)
├── infrastructure/
│   ├── docker/                   Compose stack and images
│   ├── mediamtx/                 Media gateway configuration
│   └── db/migrations.sql         Idempotent SQL script for the full schema
└── docs/
    ├── decisions/                Architecture decision records
    ├── implementation-notes/     This document and the phase reports
    └── troubleshooting/          Local setup and verification
```

## Control plane vs media plane

```text
        BROWSER                        CONTROL PLANE                    MEDIA PLANE
 ┌──────────────────┐          ┌─────────────────────────┐        ┌──────────────────┐
 │  Live Studio     │          │  ASP.NET Core API       │        │    MediaMTX      │
 │                  │          │                         │        │                  │
 │  getUserMedia ───┼──┐       │  LiveSessionService     │        │  WHIP ingest     │
 │  preview         │  │       │  LiveSessionReconciler  │        │  LL-HLS out      │
 │  RTCPeerConn ────┼──┼───────┼─────────────────────────┼───────▶│  WHEP out        │
 └──────────────────┘  │       │  IngestCredentialSvc    │  WHIP  │  recording       │
          ▲            │ REST  │  RecordingService       │        └────────┬─────────┘
          │            └──────▶│                         │                 │
          │  SignalR           │  ┌───────────────────┐  │◀────────────────┘
          └────────────────────┼──┤ StreamHealthMonitor│ │   poll /v3/paths + auth hook
                               │  └───────────────────┘  │
                               │        PostgreSQL       │
                               └─────────────────────────┘
```

The control plane never carries media packets. It authorizes access, owns session state, and reads
the gateway's status. Everything vendor-specific sits behind `IMediaGateway`.

## The broadcast flow, end to end

```text
1.  POST /live-sessions                 DRAFT      Session row + health row created
2.  POST /live-sessions/{id}/prepare    PREPARING  Gateway reachability verified
                                        READY      Recording row created if enabled
3.  Studio: getUserMedia                           Camera/mic acquired, preview shown
4.  POST .../sources/credentials                   5-minute path-scoped token minted
5.  Studio: WHIP POST → gateway                    Gateway calls back to authorize the publish
6.  POST /live-sessions/{id}/start      STARTING   Start requested
                                        LIVE       Promoted once ingest is confirmed
7.  StreamHealthMonitor, every 3s                  Reconciles state, publishes health over SignalR
8.  POST /live-sessions/{id}/stop       STOPPING   Credentials revoked, gateway path released
                                        ENDED      Recording finalized
```

Steps 3–5 may happen before or after step 6. If media is already flowing, `start` promotes to `LIVE`
immediately; if not, the health monitor promotes it when ingest appears, or fails the session after
`StartIngestTimeoutSeconds`.

## Where each responsibility lives

| Concern | Location |
|---|---|
| What state changes are legal | `LiveStream.Domain/Sessions/LiveSessionStateMachine.cs` |
| Session lifecycle and authorization | `LiveStream.Application/Sessions/LiveSessionService.cs` |
| Reconnect, degradation, health | `LiveStream.Application/Sessions/LiveSessionReconciler.cs` |
| Who may do what | `LiveStream.Domain/Identity/WorkspaceMember.cs` + `LiveSessionAuthorizationService` |
| Streaming credentials | `LiveStream.Application/Media/IngestCredentialService.cs` |
| Recording metadata | `LiveStream.Application/Recordings/RecordingService.cs` |
| Media provider | `LiveStream.Infrastructure/Media/MediaMtxGateway.cs` |
| Periodic reconciliation | `LiveStream.Api/BackgroundServices/StreamHealthMonitor.cs` |
| Realtime events | `LiveStream.Api/Realtime/` |
| Camera/microphone | `apps/web/src/hooks/useMediaDevices.ts`, `src/lib/media/devices.ts` |
| Browser publishing | `apps/web/src/lib/media/whip.ts` |
| Studio screen | `apps/web/src/components/studio/LiveStudio.tsx` |

## Reliability model

The reconciler is the single place that turns media observations into session state.

```text
STARTING  ── ingest seen ────────────────▶ LIVE
          └─ timeout exceeded ──────────▶ FAILED (LIVE_003)

LIVE      ── bitrate below floor ────────▶ DEGRADED ── recovered ──▶ LIVE
          └─ ingest lost ───────────────▶ RECONNECTING
                                              ├─ ingest returns ──▶ LIVE
                                              └─ window expires ──▶ FAILED (LIVE_005)
```

Three rules make this safe:

1. **A gateway outage is not evidence of a dropped stream.** If the control plane cannot reach the
   gateway, session state is left untouched rather than torn down.
2. **The recovery window is bounded but generous** (120s default). Within it, the session stays open
   and the studio shows a countdown.
3. **Reconciliation is idempotent.** Running it repeatedly on an unchanged stream produces no state
   change and no new events, so gateway webhooks can be lost or duplicated without harm.

## Extension points for later phases

| Phase | What plugs in | Where |
|---|---|---|
| 2 — Distribution | `IDestinationAdapter`, a `destinations` table | Alongside `IMediaGateway`; the reconciler already isolates per-source failures |
| 2 — External encoders | Enable `rtmp:`/`srt:` in the gateway config | The credential model and session model are unchanged |
| 3 — Mobile | Same REST + SignalR contracts | No server change; contracts are transport-agnostic |
| 4 — Multi-device | `live_devices`, `live_sources` tables | `LiveSession` already aggregates sources; roles exist |
| 5 — Studio | Scene/layer model | Composition is a media-plane concern behind `IMediaGateway` |
| 6 — AI | Consume `live_session_events` | Events are already an append-only stream |

Nothing in Phase 1 hard-codes a single source, a single destination, or a single media provider.

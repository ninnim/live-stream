# Phase 1 — Implementation report

**Phase:** 1 — Native Web Broadcasting
**Date:** 2026-08-30
**Objective:** the smallest real version of the platform that broadcasts directly from the web app
without OBS.

## What was implemented

A creator can sign in, create a live session, grant camera and microphone access, see a preview,
select devices, start broadcasting from the browser, watch health and viewer metrics, survive a
temporary network drop, and stop cleanly with recording metadata finalized. A viewer can watch the
result over HLS.

**OBS is not involved anywhere in that flow.** The browser publishes over WHIP (WebRTC) using
standard platform APIs.

### Starting point

The repository was **documentation only** — 28 markdown files, no source, not a git repository. There
was no existing frontend, backend, database, auth, realtime infrastructure, or test framework to
reuse or preserve, so every decision came from the blueprint rather than from existing code.

### Backend — control plane

ASP.NET Core 9 modular monolith, four projects with a strict dependency direction
(`Domain ← Application ← Infrastructure ← Api`).

- **Live Session aggregate** with a server-owned state machine covering ten states. State can only
  change through `TransitionTo`, which validates the transition and appends an event.
- **Reconciler** that turns media-plane observations into authoritative state: promotes
  `STARTING → LIVE` on ingest, drives `LIVE ⇄ DEGRADED` on sustained bitrate, moves
  `LIVE → RECONNECTING` on ingest loss, and fails only after a bounded recovery window.
- **Short-lived, path-scoped ingest credentials.** Five-minute tokens, stored only as SHA-256
  hashes, revoked when the session stops. The media gateway delegates every publish authorization
  back to this API, so no permanent stream key ever exists in a browser.
- **Authentication and tenancy**: JWT access tokens, rotating refresh tokens with replay detection,
  workspaces, and a role-to-permission table enforced through one authorization service.
- **Recording lifecycle** behind `IRecordingStore`, with a filesystem implementation that reports
  real sizes, durations, and segment counts.
- **Realtime** over SignalR at `/hubs/live`, with group membership authorized on join.
- **Health monitor** reconciling active sessions every three seconds.
- **Observability**: structured JSON logs with correlation IDs, an append-only session event log, and
  OpenTelemetry traces and metrics with a configurable OTLP exporter.

### Frontend — Live Studio

Next.js 16 + TypeScript + Tailwind.

- **Web Live Studio** matching the layout in the Phase 1 brief: preview, device pickers, camera and
  microphone state, Start Live, then the live view with duration, viewers, health, connection state,
  and Stop Live.
- **WHIP publisher** built on `RTCPeerConnection` and `fetch` — no media vendor SDK in the UI, so the
  gateway is genuinely replaceable.
- **Device handling**: enumeration, selection, permission states, mute toggles that disable rather
  than stop tracks, hot-plug detection, and fallback when a selected device disappears.
- **Reconnect**: exponential backoff with jitter, a fresh credential per attempt, and a banner that
  tells the user their session is being held open with a countdown.
- **Friendly errors**: every browser media failure is mapped to a plain-language cause and a recovery
  instruction. `NotAllowedError` never reaches a user.
- **Viewer page** with HLS playback, loading `hls.js` only where the browser needs it.

## Files and modules

| Area | Location | Scale |
|---|---|---|
| Domain | `services/api/src/LiveStream.Domain/` | Entities, enums, state machine, error codes |
| Application | `services/api/src/LiveStream.Application/` | Session, auth, credential, recording, reconciler |
| Infrastructure | `services/api/src/LiveStream.Infrastructure/` | EF Core, MediaMTX gateway, JWT, recording store |
| API | `services/api/src/LiveStream.Api/` | Endpoints, hub, middleware, health monitor |
| Tests | `services/api/tests/` | Unit, integration, database, shared doubles |
| Studio | `apps/web/src/` | App routes, components, hooks, media/realtime libraries |
| Infrastructure | `infrastructure/` | Compose stack, Dockerfiles, MediaMTX config, SQL script |
| Docs | `docs/decisions/`, `docs/implementation-notes/`, `docs/troubleshooting/` | 8 ADRs, 3 notes |

Roughly 9,100 lines of C# across 68 files and 4,000 lines of TypeScript across 32 files.

## Database changes

One migration: `InitialPhase1`. Columns, keys, and constraints are snake_case, matching
`docs/10-database-design.md`. An idempotent SQL script is committed at
`infrastructure/db/migrations.sql`.

**Tables (9):** `users`, `workspaces`, `workspace_members`, `refresh_tokens`, `live_sessions`,
`live_session_health`, `live_session_events`, `ingest_credentials`, `recordings`.

**Indexes (13),** each backing a real access path:

| Index | Access path |
|---|---|
| `ix_live_sessions_workspace_status_created` | Dashboard listing by workspace and status |
| `ix_live_sessions_media_path` (unique) | Gateway auth callback on every publish |
| `ix_live_sessions_status` | Health monitor scan every poll |
| `ix_ingest_credentials_token_hash` (unique) | Credential lookup on every publish |
| `ix_workspace_members_workspace_user` (unique) | Authorization check on every request |
| `ix_users_email`, `ix_refresh_tokens_token_hash` (unique) | Login and refresh |
| …plus 6 supporting foreign-key and lookup indexes | |

Enums are persisted as text so the database is readable and stable against renumbering.
`live_sessions.version` is an optimistic concurrency token, so two devices racing start/stop get a
conflict rather than a silent overwrite.

## API changes

15 public endpoints under `/api/v1` plus 2 internal media callbacks, and 5 server-to-client realtime
events. Full reference: `docs/implementation-notes/phase-1-api-reference.md`.

## Streaming infrastructure

**MediaMTX 1.11.3** (MIT), one container. WHIP ingest, LL-HLS and WHEP playback, fMP4 recording,
read-only control API for health, and delegated HTTP auth back into this API.

Chosen because it satisfies every Phase 1 media requirement in a single operable component while
letting the browser publish with no vendor SDK, and because its delegated auth is exactly the
credential model `docs/11-security.md` demands. Full reasoning and the alternatives considered:
`docs/decisions/0001-phase-1-media-gateway.md`.

RTMP and SRT are deliberately **disabled** in Phase 1 — native broadcasting is the required workflow,
and an unused open ingest port is only attack surface. Phase 2 enables them with a config change,
because external encoders attach to the same Live Session model.

## Tests added

**314 automated tests.**

| Suite | Tests | Covers |
|---|---|---|
| `LiveStream.UnitTests` | 170 | Exhaustive state machine (every legal transition asserted, every illegal pair asserted illegal), aggregate invariants, permissions, credential lifetime and revocation, password hashing, refresh tokens, reconnect/degradation/recovery-window behaviour, bitrate derivation, persistence and optimistic concurrency |
| `LiveStream.IntegrationTests` | 73 | Full journey over real HTTP, authorization and session isolation across every protected endpoint, media auth callback (path binding, expiry, revocation, secret transports), auth flows including refresh-replay revocation, rate limiting, idempotency, server-supplied ICE configuration. Runs with DI container validation on |
| `LiveStream.DatabaseTests` | 5 | Real PostgreSQL: migrations apply cleanly and idempotently, schema completeness, aggregate round-trip, unique constraint enforcement |
| `apps/web` (Vitest) | 59 | Device errors and fallback, WHIP publish/reconnect/backoff/teardown, ICE configuration sourcing and refresh, studio permission → preview → start → live → stop, reconnecting and failed states, formatting |
| `apps/web/e2e` (Playwright) | 7 | **The real media path**, against the running stack |

The end-to-end suite is the one that proves the product claim. A headless Chromium with a synthetic
camera publishes actual WebRTC over WHIP, and the specs cover:

- the full journey — sign in → create → capture → go live → server confirms LIVE → viewer plays HLS
  → stop → recording `READY`;
- recovery after the publisher is severed at the gateway;
- **studio reload**: state stays intact, and the broadcast resumes into the same session;
- stopping a session while it is reconnecting;
- two concurrent broadcasts staying independent;
- cross-workspace isolation against a live session.

Notable coverage elsewhere: the state machine test asserts all 26 legal transitions **and** every one
of the remaining ordered pairs as illegal, so a future edit cannot quietly widen it.

## Validation results

All commands were run in this environment, against real infrastructure.

```text
API — dotnet build LiveStream.sln -c Release
  Build succeeded. 0 Warning(s), 0 Error(s)     (warnings are errors project-wide)

API — dotnet test LiveStream.sln -c Release
  LiveStream.UnitTests         Passed!   170 passed,  0 failed
  LiveStream.IntegrationTests  Passed!    73 passed,  0 failed
  LiveStream.DatabaseTests     Passed!     5 passed,  0 failed   (real PostgreSQL 16 container)

API — dotnet-ef migrations has-pending-model-changes
  No changes have been made to the model since the last migration.

Web — npm run lint       clean
Web — npm run typecheck  clean
Web — npm test           59 passed (4 files)
Web — npm run build      Compiled successfully; 7 routes

E2E — npx playwright test (against the running Docker stack)
  7 passed
```

### The media path, verified

The whole stack was built and run with `docker compose`, and a headless Chromium with a synthetic
camera published a real WebRTC stream through it. Server-side evidence:

```text
mediamtx  session 4005de09 peer connection established
mediamtx  session 4005de09 is publishing to path 'ls_hk5q…', 2 tracks (Opus, VP8)
api       Media publish authorized session=aa2c9225… credentialId=c444d32a…
api       Live session promoted to LIVE aa2c9225…
```

The reconnect path was exercised by severing the publisher at the gateway — which is what a
broadcaster network drop looks like server-side. The session event log shows the full recovery:

```text
Starting      → Live            session went live
Live          → Reconnecting    ingest lost, session held open
ReconnectAttempted              client retrying with a fresh credential
Reconnecting  → Live            recovered inside the window
ReconnectSucceeded
```

And a recording finalized in PostgreSQL: `status=Ready, duration_seconds=2, size_bytes=15103,
segment_count=1`.

**Environment note.** The machine started with no .NET SDK, Node, or Docker. The .NET 9 SDK and
Node 24 were installed into the user profile; Docker was installed by the user partway through, at
which point the previously skipped PostgreSQL tests and the entire media path were verified for real.

## Bugs found and fixed during implementation

Seven real defects surfaced from tests and from running the real stack, not from review.

Found by the unit and integration suites:

1. **Session events were never persisted after a reload.** `LiveSessionEvent.Id` was pre-assigned, so
   EF treated events appended to a *reloaded* session as existing rows and issued an `UPDATE` against
   a row that did not exist. Every state transition after the first request would have failed with a
   concurrency exception. Fixed by letting the key be generated on insert, with a regression test
   pinning the behaviour.
2. **Refresh-token replay detection did nothing.** `RefreshAsync` revoked the token chain in memory,
   then threw before `SaveChanges` — so the revocation was discarded. A stolen token remained usable.
   Fixed by committing before the request fails.
3. **JWT issuance used the injectable clock** while validation used the system clock, so any clock
   offset produced tokens rejected as not-yet-valid. Fixed by issuing against the system clock, with
   the reasoning recorded in ADR 0003.

Found only by building and running the real stack — each of these passed every test beforehand:

4. **The API crashed on startup in a container.** `DomainExceptionHandler` is registered as a
   singleton but injected the request-scoped `CorrelationContext` — a captive dependency. Container
   validation is off in the test environment, so 241 passing tests said nothing about it. Fixed by
   resolving the correlation context per request, **and** by enabling `ValidateOnBuild` and
   `ValidateScopes` in the test host so this class of bug now fails in the suite.
5. **Every publish was denied.** The browser sent its credential as `Authorization: Bearer`, but a
   gateway using delegated HTTP auth does not forward a Bearer header to the callback. Fixed by
   sending HTTP Basic with the token as the password; the callback accepts the credential from
   several transports so a different gateway cannot silently break authorization again. The denial
   path now also logs which fields a callback carried (names only, never values), which is what made
   this diagnosable in one cycle.
6. **The Docker build failed on host build output.** `COPY services/api/ …` dragged the host's
   `obj/` into the image, overwriting the container's restore output with a `project.assets.json`
   full of Windows paths. Fixed with a `.dockerignore`.
7. **Columns were PascalCase inside snake_case tables**, contradicting `docs/10-database-design.md`
   and forcing quoted identifiers in every hand-written query. Fixed with a snake_case naming
   convention and a regenerated migration; the schema now matches its own specification.

Found by testing an acceptance criterion that had never been exercised:

8. **Reloading the control room killed the broadcast.** The page reload destroys the peer
   connection; the server correctly moved the session to `RECONNECTING`, but nothing republished, so
   it sat there until the recovery window expired and failed. The state was never *corrupt* — which
   is all criterion 10 literally asks — but the broadcast died, which contradicts the rule the
   criterion exists to protect. The studio now resumes publishing into the same session on load,
   guarded on the server reporting that nothing else is currently publishing so a second tab cannot
   fight the first for the ingest path. See ADR 0008.

The last five are the argument for the end-to-end tests now in CI: a suite that never builds a
container, never publishes a real stream, and never reloads a live page cannot catch any of them.

### One test that nearly lied

The first version of the reload spec polled for `LIVE` immediately after reloading, and passed —
against state left over from *before* the reload, because the health monitor had not yet noticed the
publisher was gone. It proved nothing. It now waits for `RECONNECTING` first, so the recovery
assertion has something real to recover from. Worth remembering whenever a test asserts a state the
system was already in.

## Known limitations

**Single instance only.** SignalR has no backplane, rate limits are per-process, and every instance
would run the health monitor. Redis (or equivalent) is required before scaling out; see ADR 0004 for
the exact three things that break.

**Recordings are not durable.** They live on a Docker volume with unlimited retention. `IRecordingStore`
exists so S3 is a drop-in, but that work is not done. See ADR 0005.

**Tokens in `localStorage`.** Deliberate and documented, with mitigations (15-minute access tokens,
refresh rotation with replay detection, no streaming credential ever persisted). `HttpOnly` cookies
are the right destination and are a coherent piece of Phase 2 work. See ADR 0007.

**Health metrics are coarse.** Bitrate is derived from byte-counter deltas; frame rate, dropped
frames, and packet loss are not yet surfaced, because the gateway's path API does not expose them.
`WebRTCSession` statistics could provide more.

**The degraded threshold needs tuning against real encoders.** During the end-to-end run, Chromium's
synthetic camera produced a low-bitrate VP8 stream that correctly classified as `DEGRADED` — the
detection works, but `PoorBitrateKbps` (400) was chosen from first principles, not from measurement.
It should be set from observed bitrates across real devices and resolutions before it drives any
user-visible promise.

**No TURN server deployed.** ICE configuration is now served by the API per credential, so adding
TURN is a configuration change with no code or frontend rebuild (ADR 0008) — but none is deployed,
and the default is a public STUN server. Restrictive networks (some corporate and mobile carriers)
still cannot connect until one is configured. The end-to-end run connected over host candidates on a
local network, which proves the media path but says nothing about symmetric NAT. **This remains the
single largest gap between "works here" and "works for everyone".**

**Adaptive bitrate is not implemented.** Degradation is *detected* and surfaced; the encoder does not
yet step down quality in response. The blueprint's hysteresis-based ladder is Phase 2+ work.

**Workspace member management has no API.** The authorization model is complete and tested, but a
workspace has exactly one member. Invitations are additive.

**No screen sharing.** Listed in `docs/01` under web capabilities but not in the Phase 1 scope
section; deferred deliberately.

## Deviations from the blueprint

**Redis deployed: no.** §61 lists it under "Build now". Every Phase 1 use has a correct in-process
equivalent, and the swap points are documented. ADR 0004.

**State machine is a superset of any single document.** The blueprint, `docs/02`, and the Phase 1
brief disagree on the state list. The union of all three was implemented, using `RECONNECTING` rather
than the blueprint's `RECOVERING` because two of the three documents and the acceptance criteria use
that name. ADR 0002.

**One external destination adapter: not implemented.** §61 lists it under "Build now", but the Phase 1
document explicitly excludes external platform publishing, and the Phase 1 agent prompt forbids it.
The narrower phase document was followed.

**RTMP ingest: not enabled.** §61 lists "RTMP ingest path" under "Build now", written when the MVP
assumed an OBS-first flow. §8A and the Phase 1 brief supersede that: native broadcasting is the
required workflow. The capability is one config line away.

## Definition of Done

| Criterion | Status |
|---|---|
| Authenticated user creates a live session | Done |
| User grants browser camera/mic permission | Done |
| User sees a local preview | Done |
| User selects supported input devices | Done |
| User starts a session | Done |
| Backend confirms LIVE state | Done — promoted only on confirmed ingest |
| Stream reaches the configured playback path | Done — verified end to end with real WebRTC media |
| A viewer can watch the live stream | Done — verified: HLS playback reached readyState in a second browser |
| Live/session health information is visible | Done |
| Temporary failures handled gracefully | Done — verified against the real stack: severed publisher recovered to LIVE |
| User can stop the stream; session becomes ENDED | Done |
| Recording metadata finalized when enabled | Done |
| Authorization enforced | Done — tested on every protected endpoint |
| Database migrations work | Done — applied to a real PostgreSQL 16 container, idempotent, drift-checked |
| Automated tests pass | Done — 304 passing, 0 skipped |
| Build passes | Done — 0 warnings with warnings-as-errors |
| Type checks pass | Done |
| Lint/static checks pass | Done |
| No critical TODOs in the implemented path | Done — none present |
| No secrets committed | Done — only `.env.example` templates |
| Documentation updated | Done — 8 ADRs, architecture map, API reference, troubleshooting guide |

## Recommended next step

Phase 1 is verified end to end, so the next work is what turns a working system into a trustworthy
one. Both items below should land **before Phase 2**, because Phase 2 doubles the failure surface.

1. **Deploy a TURN server.** The wiring is done — ICE is served by the API and refreshed per
   connection attempt — so this is now provisioning plus configuration, not development. Without it,
   a meaningful share of real users on corporate and mobile networks simply cannot connect. This is
   the difference between a demo and a product.
2. **Reliability targets with evidence.** The blueprint (§48) asks for stream-start success rate,
   reconnection success rate, and recording completion rate. The metrics are emitted; nothing yet
   aggregates or alerts on them. The same exercise should set `PoorBitrateKbps` from measured
   device bitrates rather than the current first-principles guess.

**Then Phase 2 — Multi-Platform Distribution.** The groundwork is already in place: destination
failures are architecturally separable from the core session, adapters have a natural home beside
`IMediaGateway`, and enabling RTMP output is a configuration change. The order suggested by
`implementation/phase-2-multi-platform-distribution.md` is sound: destination model and adapter
interface first, Custom RTMP as the proving adapter, then the OAuth-based providers.

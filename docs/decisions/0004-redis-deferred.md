# ADR 0004 — Redis deferred beyond Phase 1

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

`MASTER_BLUEPRINT.md` §27 lists Redis in the recommended stack for "cache, locks, presence, ephemeral
session state, and rate limiting". §61 lists it under "Build now".

The dependency rules also say: check whether the project already has an equivalent, and avoid adding
dependencies without a clear Phase 1 requirement.

## Decision

Do not deploy Redis in Phase 1. Use in-process equivalents, chosen so the swap is a configuration
change rather than a redesign.

| Blueprint use | Phase 1 implementation | Path to Redis |
|---|---|---|
| Rate limiting | ASP.NET Core's built-in `RateLimiter` middleware | Replace the partition store |
| Caching | `IDistributedCache` (memory-backed by default) | `AddStackExchangeRedisCache` — one line |
| Ephemeral session state | `live_session_health`, one row per session overwritten in place | Unchanged; low-volume and transactional |
| Presence / viewer counts | Read from the media gateway's control API | Unchanged |
| SignalR scale-out | Not needed for a single instance | `AddStackExchangeRedisBackplane` |

## Rationale

Every Redis use case in Phase 1 has a correct in-process answer, and none of them are on a path that
Redis would make *more* correct at this size. Health state is one row per active session updated
every few seconds — transactional data, not a telemetry firehose, so
`docs/02-system-architecture.md` principle 7 is not violated.

Deploying a cache nobody reads would add an operational dependency, a failure mode, and a container,
in exchange for nothing observable.

## Consequences — the point at which Redis becomes required

Phase 1 is **single-instance**. Running more than one API instance requires Redis (or an equivalent)
for three specific things:

1. **SignalR backplane.** Without it, a realtime event published by instance A never reaches a studio
   connected to instance B. The studio's REST polling would mask this as staleness rather than an
   outage, which makes it easy to miss.
2. **Distributed rate limiting.** Per-instance limits multiply by the instance count.
3. **Health monitor coordination.** Every instance currently runs `StreamHealthMonitor` and would
   reconcile the same sessions. The reconciler is idempotent, so this is wasteful rather than
   incorrect, but it should be leader-elected or partitioned before scaling out.

These are recorded as known limitations in the Phase 1 report.

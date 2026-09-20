# ADR 0002 — Reconciling the Live Session state machine

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

Three source documents describe the session lifecycle, and they do not agree.

`MASTER_BLUEPRINT.md` §12:

```text
DRAFT -> READY -> STARTING -> LIVE -> DEGRADED -> RECOVERING -> LIVE -> STOPPING -> ENDED
plus a separate FAILED state
```

`docs/02-system-architecture.md`:

```text
DRAFT -> PREPARING -> READY -> LIVE -> RECONNECTING -> LIVE -> STOPPING -> ENDED
Failure paths: PREPARING -> FAILED, READY -> FAILED, LIVE -> FAILED (unrecoverable only)
```

`MASTER_BLUEPRINT.md` §11.3 additionally distinguishes `LIVE`, `DEGRADED`, `RECONNECTING`, `ENDED`,
`FAILED`.

The blueprint is the higher authority (`README.md`, `ai/implementation-workflow.md`), but the
architecture document contributes `PREPARING`, which the blueprint's list lacks, and the blueprint
contributes `STARTING` and `DEGRADED`, which the architecture document lacks.

## Decision

Implement the **union** of the three, which is a strict superset of every documented path:

```text
DRAFT ──▶ PREPARING ──▶ READY ──▶ STARTING ──▶ LIVE ⇄ DEGRADED
                                                 │       │
                                                 └──▶ RECONNECTING ──▶ LIVE
                                                          │
                                          ┌───────────────┴──────────────┐
                                          ▼                              ▼
                                      STOPPING ──▶ ENDED             FAILED
```

Naming choices:

- **`RECONNECTING`**, not the blueprint's `RECOVERING`. Two of the three documents use
  `RECONNECTING`, `docs/09` and the Phase 1 specification both use it, and the Phase 1 acceptance
  criteria name it explicitly ("moves the session to RECONNECTING when recoverable").
- **`PREPARING`** is kept: media resource allocation can fail, and it needs a state so a stuck
  allocation is visible rather than looking like an unexplained pause in `DRAFT`.
- **`STARTING`** is kept: it is the gap between the user pressing Start and the media plane
  confirming ingest, and `docs/04` requires that the client not display "Live" during it.

The transition table lives in one place, `LiveSessionStateMachine`, and is the only thing that may
change `LiveSession.Status`. Every legal transition and every illegal pair is asserted by
`LiveSessionStateMachineTests`.

## Rules that follow from the product requirements

**`LIVE` cannot reach `ENDED` directly.** Stopping is what triggers media teardown, credential
revocation, and recording finalization. Skipping `STOPPING` would orphan all three.

**`RECONNECTING` cannot reach `ENDED` at all.** A recovering session either returns to `LIVE`, is
stopped deliberately by the user, or expires into `FAILED`. This encodes the blueprint's rule that a
temporary network outage must never be silently converted into a finished broadcast.

**`READY` may return to `PREPARING`.** Re-preparing lets a studio reload re-verify that media
resources still exist without inventing a new state.

**Terminal states have no outgoing transitions.** `ENDED` and `FAILED` are final; a new broadcast is
a new session.

## Consequences

- The status vocabulary is larger than any single document specifies. Each extra state earns its
  place by representing a distinct, observable operational condition.
- Clients must handle ten statuses. `LiveSessionStatusPayload.allowedTransitions` is returned with
  every status read so a client can render available actions without duplicating the table.
- `LiveSessionStateMachine.IsBroadcasting` and `ExpectsIngest` group states by behaviour, so callers
  ask questions about capability instead of enumerating statuses.

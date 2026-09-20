# Phase 4 — Multi-Device & Collaboration: implementation report

## What was built

A producer can bring a phone, a laptop screen, or a colleague into a live session by showing a code.
The device scans or types it, joins, and starts sending — with no account, no app, and no credential
that outlives the show.

```text
Control room                      Device (phone)
     │  "Add device" → code + QR       │
     │ ─────────────────────────────▶  │  scans /join/ABCD-EFGH
     │                                 │
     │           POST /pairings/claim  │  ← device token (12h, source-scoped)
     │ ◀───────────────────────────────│
     │                                 │  POST /device/credentials  (per attempt)
     │  sees "Joined"                  │  WHIP ──▶ its own gateway path
     │  sees "Sending"  ◀── observed from the media plane, not declared
     │                                 │
     │  "Remove"  ──▶ secrets erased · credentials revoked · publisher kicked
```

## Acceptance criterion

From `implementation/phase-4-multi-device-and-collaboration.md`:

> *Multiple authorized devices can join one session and expose their source/status to the control
> room without sharing permanent credentials.*

**Verified end to end, in two real browsers, with real media.** During the run the gateway logged two
independent publishers on two distinct paths at the same time:

```
[WebRTC] [session 1d4920c1] is publishing to path 'ls_oka51tz1yw8os51l6rcwj1', 2 tracks (Opus, VP8)
[WebRTC] [session c5bdc26f] is publishing to path 'ls_905zmo75q4sob894b1j54o', 2 tracks (Opus, VP8)
```

— the studio and a paired phone, contributing simultaneously to one session.

| Scope item | Status |
|---|---|
| QR / pairing flow | Done — code, QR image, join link, countdown, single use |
| Device roles | Done — 7 roles, capabilities as pure data |
| Permission checks | Done — enforced server-side per role and per workspace |
| Source presence and health | Done — observed from the media plane on its own loop |
| Operator controls | Done — invite, rename, remove |
| Moderator role | Done — joins, sees the session, cannot publish |
| Device revoke | Done — immediate, including severing live media |

## What is verified

- **The journey**, in two browser contexts: invite → scan → join → publish → appears in the control
  room → removed → locked out. Real WebRTC into a real gateway.
- **Revocation is immediate.** The device token works on the request before the operator presses
  Remove and returns 401 on the request after it.
- **Neither secret is ever returned twice or stored in the clear** — asserted against raw JSON and
  against the database columns.
- **A code cannot be used twice**, and expired, spent and never-issued codes are indistinguishable.
- **Scheme separation**: a device token presented as `Bearer` is refused, and a user token presented
  as `Device` is refused.
- **A device dropping out does not disturb the session** it is contributing to.
- 398 backend tests (277 unit, 121 integration), 97 frontend tests, 13 end-to-end tests.

## Two things the real run changed

**A device was renaming itself over the operator's choice.** The pairing call accepted a device
label and applied it to the display name, so "Phone camera" silently became "Windows PC" when the
phone joined. The operator's name now always wins; the device's platform is recorded in the pairing
audit event instead, and the join screen no longer has a name field at all.

**A test was passing on the button it had just clicked.** `getByText("Sending")` matches
case-insensitive substrings, so it also matched the phone's own "Start sending video" button — the
assertion would have passed whether or not any media flowed. All status assertions are now exact.

## The gateway crash worth knowing about

Program switching was going to be implemented by repointing a proxied path through MediaMTX's config
API. Adding a path at runtime **panics MediaMTX 1.11.3 and takes the entire media plane down**:

```
panic: runtime error: invalid memory address or nil pointer dereference
github.com/bluenviron/mediamtx/internal/recordcleaner.(*Cleaner).ReloadPathConfs(...)
```

Every session dies with it. **Never mutate gateway path configuration at runtime.** Found by trying
it, which is the only way this shows up.

## What is not in this phase

**Program switching** — choosing which source viewers watch. Sources can be seen, monitored and
managed; the model carries `IsProgram` and the API exposes it, but nothing changes the program feed
yet.

Deferred deliberately: doing it properly means the studio stops publishing directly into the session
path and a relay feeds it instead, which reworks Phase 1's verified media flow. That belongs with the
composition pipeline in Phase 5, and `docs/05-multi-device.md` places it there too — *"Full
multi-source composition and automatic switching belongs in the professional studio phase."*

See [ADR 0010](../decisions/0010-multi-device-contribution.md) for the full reasoning and the route
that remains open.

## Known limitations

- **No program switching**, as above.
- **No mid-session role change.** Changing a device's role means removing it and issuing a new code.
- **Device tokens are not refreshable.** After 12 hours a device pairs again. Deliberate: a
  refreshable device credential is most of the way back to a permanent one.
- **Pairing rate limiting is per-instance.** Like every other limit here, it needs a shared store to
  hold across replicas ([ADR 0004](../decisions/0004-redis-deferred.md)).
- **Screen share is a role, not a capture mode.** A device paired as `Screen` publishes whatever its
  camera picker gives it; `getDisplayMedia` is not wired up yet.

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `Sources__PairingCodeLifetimeSeconds` | 600 | How long a code stays redeemable |
| `Sources__DeviceTokenLifetimeSeconds` | 43200 | How long a paired device stays authenticated |
| `Sources__MaxSourcesPerSession` | 8 | Including the studio |
| `Sources__SourcePresenceGraceSeconds` | 20 | Before a silent device is called disconnected |
| `JoinLink__BaseUrl` | `http://localhost:3000` | The address a QR code points at — must be reachable **from the phone** |
| `RateLimits__PairingAttemptsPerMinute` | 10 | Load-bearing; see ADR 0010 |

`JoinLink__BaseUrl` is the one that bites in practice: a QR pointing at `localhost` is unreachable
from a phone. Set it to the address devices can actually resolve.

## Where to look

| Concern | File |
|---|---|
| Source lifecycle rules | `services/api/src/LiveStream.Domain/Sources/SourceStateMachine.cs` |
| Pairing, tokens, revocation | `.../Domain/Sources/SessionSource.cs` |
| Role capabilities | `.../Domain/Sources/SourceEnums.cs` |
| Invite, claim, revoke | `services/api/src/LiveStream.Application/Sources/SourceService.cs` |
| Presence observation | `.../Application/Sources/SourceReconciler.cs` |
| Device authentication | `services/api/src/LiveStream.Api/Security/DeviceAuthentication.cs` |
| Control-room UI | `apps/web/src/components/studio/SourcePanel.tsx` |
| What a phone sees | `apps/web/src/components/device/JoinClient.tsx` |

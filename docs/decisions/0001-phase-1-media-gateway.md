# ADR 0001 — MediaMTX as the Phase 1 media gateway

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

`MASTER_BLUEPRINT.md` §8A makes native broadcasting mandatory: a creator must be able to stream from
the web application without installing OBS. `docs/03-streaming-engine.md` narrows this further:

- prefer **WebRTC-based contribution** for native browser capture;
- deliver to viewers over **HLS/LL-HLS** for scale, WebRTC where low latency is required;
- record to durable storage;
- issue **short-lived, scoped credentials** rather than permanent stream keys;
- keep the media implementation behind an interface (`ai/architecture-rules.md`).

The blueprint deliberately does not name a media server: "The exact media server should be selected
based on required scale, protocol coverage, operational expertise, and licensing" (§27).

Two further constraints shaped the decision:

- `ai/coding-rules.md` rule 3 — never invent infrastructure the repository cannot run;
- the dependency rules — do not add a large framework for a small problem.

## Decision

Use **MediaMTX** (MIT licensed) as the Phase 1 media gateway, behind the `IMediaGateway` interface.

Browsers publish over **WHIP** (WebRTC-HTTP Ingestion Protocol). Viewers receive **LL-HLS** or
**WHEP**. MediaMTX records to a volume the API reads when finalizing recording metadata.

## Why this option

| Requirement | How MediaMTX satisfies it |
|---|---|
| Native browser contribution, no OBS | WHIP ingest driven by the browser's own `RTCPeerConnection` — no vendor SDK in the UI |
| Scalable playback | LL-HLS out of the box |
| Low-latency playback | WHEP out of the box |
| Recording | Native fMP4 segment recording |
| Health/viewer metrics | Read-only HTTP control API (`/v3/paths/get/{name}`, `/v3/webrtcsessions/list`) |
| Short-lived scoped credentials | Delegated HTTP auth: the gateway asks *our* API to authorize every publish |
| Operable by a small team | One container, one config file |
| Future external encoders | RTMP and SRT already supported; Phase 2 enables them with a config change |

The decisive property is **delegated authentication**. Because MediaMTX asks the control plane to
authorize each publish, no long-lived stream key ever exists in a browser, and revoking a session's
credentials takes effect immediately. That is exactly what `docs/11-security.md` requires, and it is
what makes the WHIP path safe to expose to end users.

The second decisive property is that WHIP needs **no client library**. The studio uses standard
browser APIs, so the media vendor is genuinely swappable — the anti-pattern "place RTMP/codec
details inside UI components" is avoided structurally rather than by convention.

## Alternatives considered

**LiveKit.** Excellent SFU with WHIP support, but it is a full real-time platform: rooms,
participants, its own client SDKs, and a Redis dependency for scale-out. Phase 1 needs one publisher
and HLS viewers. Adopting it would mean taking a large framework for a small problem and coupling the
studio to a vendor SDK. Worth revisiting at Phase 3 (multi-device) or Phase 4 (guests), where its
participant model earns its complexity.

**mediasoup / Janus.** Powerful, but both require writing and operating a bespoke media server
application. That is a Phase 0 research project, not a Phase 1 deliverable.

**OvenMediaEngine.** A close match on features (WHIP in, LL-HLS out). MediaMTX was preferred for its
simpler single-binary operation and its HTTP auth hook, which maps directly onto our credential model.

**Cloud provider (AWS IVS, Mux, Cloudflare Stream).** Fast to integrate and operationally cheap, but
most are RTMP-ingest-first, which would push the browser back toward an encoder or a proprietary SDK.
It also puts the core media path behind a vendor before the product has proven its own reliability
targets. A managed provider remains a viable swap later precisely because `IMediaGateway` exists.

## Consequences

- The control plane never touches media packets; it only reads state and authorizes access. This
  preserves the control-plane/media-plane split the blueprint requires.
- Stream health is derived from byte counters observed over time rather than from encoder telemetry.
  It is accurate enough to classify GOOD/FAIR/POOR and detect ingest loss, but it is not a substitute
  for per-frame encoder statistics. Recorded as a known limitation.
- The path name, not a token, is the capability for public and unlisted playback. Path names are
  random and unguessable, and can be rotated (`LiveSession.RotateMediaPath`).
- Replacing the provider means implementing `IMediaGateway` and changing configuration. No session,
  UI, or database change is required.

## Verification status

**Verified end to end against a running stack.** A headless Chromium with a synthetic camera
published real WebRTC media over WHIP; the gateway authorized it through this API's callback and the
session was promoted to LIVE, played back over HLS, then stopped with a finalized recording:

```text
mediamtx  session 4005de09 is publishing to path 'ls_hk5q…', 2 tracks (Opus, VP8)
api       Media publish authorized session=aa2c9225… credentialId=c444d32a…
api       Live session promoted to LIVE aa2c9225…
```

Covered by `apps/web/e2e/broadcast-journey.spec.ts`, which runs in CI.

### One correction the real run forced

The browser originally sent its credential as `Authorization: Bearer`. MediaMTX only populates the
callback's `token` field when it is validating JWTs itself; under delegated HTTP auth a Bearer header
is not forwarded, so every publish was denied with "no token presented". The publisher now sends
**HTTP Basic** with the token as the password, which the gateway does forward. The callback endpoint
accepts the credential from the token field, the password field, or a query parameter, so a different
gateway's transport will not silently break authorization again.

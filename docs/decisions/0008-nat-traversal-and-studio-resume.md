# ADR 0008 — NAT traversal configuration and studio resume

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

Two decisions that came out of running Phase 1 against real infrastructure rather than a fake
gateway. Both concern the same theme: what happens when the broadcaster's connection is not ideal.

## 1. ICE configuration is served by the API

### Context

The WHIP publisher originally hard-coded a public STUN server in the browser bundle. That is two
problems in one:

- it puts an infrastructure address inside a UI component, which `ai/architecture-rules.md` lists as
  an anti-pattern;
- it makes TURN impossible to add without shipping a new frontend build, and TURN credentials are
  secrets that must never be compiled into a bundle every viewer can read.

### Decision

The API returns `iceServers` alongside every ingest credential
(`POST /api/v1/live-sessions/{id}/sources/credentials`), configured under `Media:Ice`.

Because the publisher requests a fresh credential for **every** connection attempt, ICE
configuration is refreshed on every reconnect too. That is exactly the handling a TURN credential
needs: short-lived, issued to an authenticated and rate-limited caller, never persisted.

The browser bundle now contains no infrastructure address at all.

### Adding TURN

```jsonc
"Media": {
  "Ice": {
    "Servers": [
      { "Urls": [ "stun:stun.example.com:3478" ] },
      {
        "Urls": [ "turn:turn.example.com:3478?transport=udp" ],
        "Username": "…",          // from a secret store, not committed
        "Credential": "…"
      }
    ]
  }
}
```

### Consequences

- **TURN is a configuration change, not a code change.** No TURN server is deployed in Phase 1; the
  default is a public STUN server, which is sufficient on ordinary home and office networks.
- **Restrictive networks still cannot connect until TURN is configured.** This remains the single
  largest gap between "works here" and "works for everyone", and is the first recommended step after
  Phase 1. The end-to-end run connected over host candidates on a local network, which proves the
  media path but says nothing about symmetric NAT.
- Ephemeral, time-limited TURN credentials (the standard HMAC scheme) fit this shape without further
  changes: the credential endpoint is already the place that mints short-lived secrets per attempt.

## 2. A reloaded studio resumes its broadcast

### Context

Acceptance criterion 10 of the Phase 1 specification: *"Refreshing the control room does not corrupt
session state."*

Testing it against the real stack showed something worse than corruption. A reload destroys the page
and with it the `RTCPeerConnection`. The server correctly observed ingest loss and moved the session
to `RECONNECTING` — and then nothing republished, so the session sat there until the recovery window
expired and it `FAILED`. The state was never corrupt; the broadcast simply died.

That contradicts the intent behind the rule. A reload is precisely the kind of recoverable
interruption `MASTER_BLUEPRINT.md` §11.3 says must not end a session.

### Decision

On load, if the server reports the session is broadcasting **and** that nothing is currently
publishing, the studio re-acquires media and republishes into the same session.

It deliberately does **not** call `start`: the session is already past that transition, and the
server owns its state. Only the media transport is re-established.

The guard on `health.ingestConnected == false` is what makes this safe. Opening the studio in a
second tab while the first is still publishing sees `ingestConnected == true` and does nothing, so
the two tabs do not fight over the ingest path.

### Consequences

- A reload costs a few seconds of stream interruption instead of the whole broadcast. Session id,
  start time, duration, viewer count, and recording all continue unbroken.
- The browser re-prompts for camera access only if permission was actually revoked; an
  already-granted permission is re-issued silently.
- If republishing fails, the studio surfaces a plain-language error and stops retrying, rather than
  looping against a path it cannot obtain.
- Covered by `apps/web/e2e/studio-resilience.spec.ts`, which reloads a live studio, waits for the
  server to genuinely observe the drop, and then asserts recovery to `LIVE` with an unchanged
  `startedAt`.

### A note on the test that nearly lied

The first version of that spec polled for `LIVE` immediately after the reload. It passed — against
state left over from *before* the reload, because the health monitor had not yet noticed the
publisher was gone. It proved nothing.

The spec now waits for `RECONNECTING` first, so the recovery assertion has something real to
recover from. Worth remembering whenever a test asserts a state the system was already in.

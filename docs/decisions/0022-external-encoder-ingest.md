# 0022 — External encoder ingest and session stream keys

Status: accepted
Date: 2026-09-20
Extends ADR 0003 (authentication and tenancy) with a second, deliberately longer-lived publish
credential. Does not change the browser credential model.

## Context

A creator asked how to stream a mobile game. The answer, before this, was that they could not.

The reason is a platform limitation rather than a gap in the product: **no browser on any phone can
record another app.** Capturing a game running outside the browser needs an OS-level API —
Android's `MediaProjection`, iOS's ReplayKit — available only to native applications. iOS Safari
does not implement `getDisplayMedia` at all, and even where a mobile browser did, it would capture
browser content, not a game. Phase 3's mobile broadcaster (ADR 0021) publishes the *camera*, and no
amount of work on it reaches the screen.

What can capture a phone game is an encoder: a screen-capture app on the phone, OBS on a desktop, a
console through a capture card. Every one of them speaks RTMP. The platform already modelled them —
`MASTER_BLUEPRINT.md` §14 lists optional external encoder compatibility, and the gateway config
carried a note saying RTMP "can be enabled later without any other change" — but the port was off
and no credential existed for it.

## Decision

### 1. Enable RTMP and SRT ingest on the media gateway

Both are authorized by the same control-plane callback as every other publisher. MediaMTX's
`authHTTPAddress` applies to all protocols, and the callback already accepted a token in the
password field or query string, so an RTMP publisher is checked exactly as a WHIP publisher is.
An anonymous connection gets no further than the handshake.

SRT is offered alongside RTMP because it holds a lossy mobile uplink together far better, which is
the case this exists for. Its credentials travel in the stream id rather than a query string.

**Off is still a supported configuration, and the default in the API.** With
`Media__MediaMtx__PublicRtmpUrl` unset, no key can be issued and the studio offers no encoder
instructions at all. A deployment that does not want an open ingest port sets nothing and loses
nothing else.

### 2. A session-scoped stream key, which is a real departure

ADR 0003 issues publish credentials that live for five minutes, because the browser silently mints
another on every reconnect. An encoder cannot do that: the key is typed in by hand, and re-presented
on every reconnect for the length of a show. A five-minute key would drop the broadcast and could
not be renewed without the person stopping to retype it.

So `IngestCredentialScope.StreamKey` lives **twelve hours by default**, and that is the point rather
than an oversight. What makes it acceptable is that every other property is tightened instead:

- **Bound to one session's path**, like every other credential. A key cannot publish into another
  session.
- **Rotatable, and rotation is the only way to see it again.** The plaintext is returned once and
  never stored — only a SHA-256 hash — so "show me my key" and "the old one is compromised" are the
  same button. That is what lets somebody who pasted a key into the wrong window fix it themselves.
- **Revocable on its own**, without touching browser credentials. "Stop that encoder" and "cut off
  all access to this session" are different intentions.
- **Dead when the session ends**, whatever its remaining lifetime says. A key that outlives its
  session would be the permanent stream key this platform set out not to have.
- **Useless until the session expects ingest.** The key exists from the moment it is asked for; it
  opens nothing until the session has been prepared.
- **Capped at one week** by configuration, so no deployment can quietly turn it into a permanent
  secret.

### 3. Going live without a local publisher

`Start Live` previously required local capture, because a session with nothing publishing would sit
in STARTING until it timed out. An encoder is that something, arriving over RTMP, so the studio
gains an external go-live path: prepare, then start, with no `WhipPublisher` involved. The session
still sits in STARTING until the server sees ingest — which is also what makes a wrong key visible,
rather than appearing to work.

Creating a key prepares the session, because a key that opens nothing on its first attempt is
reported by encoders as a bare connection failure and reads to everybody as a wrong key.

### 4. A browser holding an encoder key never auto-resumes

Both studios re-publish their camera when the server reports ingest down inside a live session —
the reloaded-studio recovery from Phase 1. That is wrong when an encoder is the source: ingest down
there means the encoder is reconnecting, and republishing the camera would take the session away
from the game the audience came for. Both studios now skip that recovery while a stream key is held.

**A known interaction, stated rather than hidden:** if the page is *reloaded* mid-encoder-broadcast,
it no longer holds the key — the plaintext lives only in page memory — so it cannot tell that an
encoder is the source, and the Phase 1 recovery applies as before. MediaMTX's default
`overridePublisher` then lets the browser take the path. Making the source kind durable (a flag on
the session, or reading it back from session events) is the fix, and it is deliberately not in this
change: it touches the session aggregate, and the narrow case does not justify that here.

## Consequences

- A creator can stream a mobile game, a console, or an OBS scene into the same live session, with
  the same distribution, recording and viewer pages as every other broadcast.
- Port 1935 is open where the feature is enabled. This is the first ingest port on this platform
  that accepts a TCP connection before any credential is examined.
- RTMP is unencrypted. The credential is a rotatable, session-scoped, single-path key rather than an
  account password, which is what makes that tolerable on a trusted network; a deployment carrying
  encoder traffic across the internet should front it with RTMPS or prefer SRT, and that is a
  configuration change rather than a code one.
- The e2e compose overlay's stand-in platform moved to host port 1936, because the real gateway now
  takes 1935.

## Verification

Proven against the running stack rather than asserted:

- FFmpeg publishing to the issued URL put a session **LIVE** with `ingestConnected: true` at
  5.6 Mbps; the gateway logged `is publishing to path ... 2 tracks (H264, MPEG-4 Audio)`.
- The same URL with one character of the key changed was refused: the gateway logged
  `authentication failed: server replied with code 401`, the control plane logged
  `Media publish denied: unknown credential`, and the session stayed READY.
- SRT with the issued stream id published successfully, confirming the `publish:path:user:pass`
  syntax rather than trusting documentation for it.
- `ffmpeg` exits **0** when an RTMP server closes the connection on it, so an encoder not
  complaining is no evidence a key was accepted. Every test asserts on session state instead.

## References

- `docs/decisions/0021-mobile-broadcasting.md` — why the phone's camera is all a browser can send
- `docs/decisions/0003-authentication-and-tenancy.md` — the credential model this extends
- `docs/troubleshooting/streaming-a-game-from-your-phone.md`
- `MASTER_BLUEPRINT.md` §14

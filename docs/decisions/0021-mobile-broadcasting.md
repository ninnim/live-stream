# 0021 — Mobile broadcasting

Status: accepted
Date: 2026-09-20
Supersedes nothing. Narrows ADR 0011 §7 for mobile only.

## Context

Phase 3 asks for a broadcaster a creator can go live from on Android and iOS without OBS or a
desktop, and describes it as "a React Native app or agreed native architecture"
(`implementation/phase-3-mobile-broadcasting.md`). Phases 1–2 and 4–7 are implemented; this was the
remaining gap.

The platform already has everything a phone broadcast needs below the interface: WHIP publishing
straight out of the browser (`@/lib/media/whip`), short-lived per-attempt ingest credentials, the
live-session state machine, reconnect with server-side recovery windows, distribution, and
recording. A phone broadcaster is therefore not a second media stack. The question is only what
sits on top of it.

## Decision

### 1. The mobile broadcaster is the web application, made phone-shaped and installable

A second screen in `apps/web` — `MobileStudio` — selected at runtime by form factor, over the same
control plane, the same WHIP ingest and the same session aggregate as the desktop studio. It is
installable as a PWA, so it launches without browser chrome from a home screen.

Why not React Native:

- **It would be unverifiable here.** This repository is built entirely in containers; the
  development machine has no Node, no Android SDK and no Xcode. A React Native app could be
  written but not compiled, run or tested, and `ai/coding-rules.md` §3 and §11 exist precisely to
  stop that — "never invent infrastructure the repository cannot run", "do not claim a feature
  works without running the relevant test/build commands".
- **The media path is already native to the device.** `RTCPeerConnection` in a mobile browser uses
  the same platform WebRTC stack a React Native library would wrap, including hardware H.264. The
  gap between web and native here is the interface and OS integration, not the encoder.
- **It keeps one implementation of the rules.** Session states, credential handling, reconnect
  policy and destination behaviour exist once. Two clients means two chances to diverge on
  exactly the behaviour ADR 0002 and ADR 0009 are careful about.

The cost is stated plainly rather than hidden: a PWA cannot broadcast from the background, cannot
publish to an app store, and on iOS depends on Safari's WebRTC. `docs/08-mobile-app.md` already
requires that background capture not be assumed, and the mobile capture layer is deliberately
isolated (`@/hooks/useMobileCapture`, `@/lib/media/mobile`) so a native shell can replace it later
without touching the session logic above it.

### 2. Quality adapts itself on mobile, and only on mobile

ADR 0011 §7 makes lowering quality the operator's decision, because a resolution drop mid-sentence
is indistinguishable from a fault and the operator knows whether the shot matters more than the
smoothness. That reasoning depends on there *being* an operator watching. On a phone there is not:
the person is holding the camera and looking at what they are filming.

So on mobile the ladder in `@/lib/media/adaptive` steps the encoder down after roughly six seconds
of sustained strain, says so in one sentence, and climbs back after roughly thirty seconds of calm.
The desktop studio's policy is unchanged.

Three properties make this acceptable rather than merely automatic:

- **Nothing is renegotiated.** Rungs are applied with `RTCRtpSender.setParameters` — bitrate,
  `scaleResolutionDownBy`, `maxFramerate` — inside the connection that is already up. The picture
  softens; the stream does not gap. Re-opening the camera at a lower rung, which is what the
  desktop quality selector does, would black the outgoing video out, and doing that automatically
  would be worse than the problem it solves.
- **It falls faster than it climbs.** Three strained samples step down; fifteen calm ones step up,
  with a longer hold after a climb. Oscillation looks worse to an audience than sitting one rung
  low.
- **It is always overridable.** Choosing a rung by hand resets the ladder and re-opens the camera
  at what was asked for.

### 3. Capture is 30fps, and 1080p is never chosen automatically

A phone captures at 30 (`MOBILE_FRAME_RATE`) rather than the studio's 60: doubling the frame rate
roughly doubles the bitrate the uplink must carry and the heat the encoder makes, spending the two
things a mobile broadcast has least of on frames a handheld shot never needed.

The starting rung is 720p, or 540p on evidence of a poor connection, an old device or Data Saver.
1080p is offered but never selected automatically, because **nothing available before publishing
measures the uplink** — `navigator.connection.downlink` is a downstream estimate, and a link that
pulls 50 Mbps can push 1. Starting conservatively and letting the measured ladder decide is
recoverable within seconds; starting high and stuttering is what an audience remembers.

### 4. The phone keeps its own screen awake, and repairs its own camera

Two mobile-only failures the desktop studio has no equivalent of:

- **The screen locking suspends capture.** The wake lock is held only while on air
  (`@/hooks/useWakeLock`), and released after, so a phone left on a finished session still sleeps.
- **The OS takes the camera** for a call or an app switch, and iOS reports it by *muting* the
  track rather than ending it — so nothing in the WebRTC stack notices and the audience watches a
  frozen frame. The capture layer re-opens the camera whenever the page becomes visible and the
  track is muted or ended, and the viewfinder says what happened.

### 5. The two studios are views, not products

One session, reachable from either. `?view=mobile` and `?view=desktop` override detection and are
remembered, and each screen links to the other. Production features that need a mouse and a
keyboard — scenes, overlays, the compositor, multi-device sources, AI operations — stay on the
desktop studio rather than appearing half-built on a phone. The mobile screen carries what somebody
filming actually needs: go live, stop, flip, mute, quality, connection health, and destination
start/stop.

The compositor in particular is deliberately absent from the mobile path: composing a program on a
canvas at 30fps and mixing audio through a Web Audio graph is a second encoder's worth of work, on
the device least able to spare it, for a layout nobody can build one-handed.

## Consequences

- A creator can go live from a phone with no app install, and installing the PWA is an improvement
  rather than a requirement.
- Phase 3's "network-aware quality" is delivered as measurement-driven adaptation rather than as a
  pre-flight guess, which is the only honest form of it given what browsers expose.
- Background broadcasting is not supported, and is documented as not supported.
- `WhipPublisher` gains `applyEncoding`, which the desktop studio does not use today. It is a real
  integration boundary rather than a speculative one: the ladder needs it, and the limits survive
  reconnects because a reconnect is when they matter most.
- A native shell, if it is ever wanted, replaces `useMobileCapture` and the components above it and
  keeps everything else.

## References

- `implementation/phase-3-mobile-broadcasting.md`
- `docs/08-mobile-app.md`
- `docs/decisions/0011-live-capture-switching.md` §7
- `docs/decisions/0017-broadcast-quality.md`
- `docs/implementation-notes/phase-3-report.md`

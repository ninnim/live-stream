# Phase 3 — Mobile Broadcasting: implementation report

Phase 3 was the one gap in the delivery sequence: phases 1–2 and 4–7 were implemented, and a
creator could broadcast from a desktop browser but not from a phone. This closes it.

## What a creator can now do

Open a session on a phone and broadcast from it. Sign in, create or open a session, allow the
camera, tap **Go live**, film. Flip between the front and back camera mid-broadcast without the
audience seeing a break, mute, turn the picture off, change quality, watch the connection, start
and stop destinations, and stop. The screen stays awake while on air, the camera comes back by
itself after a phone call, and the quality adapts to the network without anyone having to notice
that it should.

The acceptance criterion — *a creator can start a live session from supported Android and iOS
devices without OBS or desktop software* — is met by the web application itself, installable to a
home screen. ADR 0021 records why that rather than React Native, and what it costs.

## Shape

```text
apps/web/src/lib/media/mobile.ts        Pure policy: form factor, mobile rungs, starting quality
apps/web/src/lib/media/adaptive.ts      Pure policy: the encoder ladder and when it moves
apps/web/src/hooks/useMobileCapture.ts  Mobile capture layer: facing, lifecycle, interruptions
apps/web/src/hooks/useAdaptiveQuality.ts  Turns measurements into encoder changes
apps/web/src/hooks/useWakeLock.ts       Keeps the screen awake while on air
apps/web/src/hooks/useFormFactor.ts     Which broadcaster this device gets
apps/web/src/components/mobile/         The phone screen: viewfinder, status, controls, sheet
apps/web/src/components/studio/StudioShell.tsx  Picks between the two
apps/web/public/manifest.webmanifest    Installable to a home screen
```

Both policy modules are pure — signals in, decision out — so the interesting parts are tested
without a browser, a phone or a network.

## Decisions worth knowing about

**The media plane did not change.** The phone publishes over the same WHIP path, with the same
short-lived per-attempt credentials, into the same session aggregate. A broadcast started on a
phone is indistinguishable from one started at a desk, and can be taken over from the full studio
while it is running. The only change to `WhipPublisher` is `applyEncoding`, below.

**Quality adapts itself, and only on mobile.** ADR 0011 §7 makes quality drops the operator's
decision because an unexplained resolution change reads as a fault. That holds where somebody is
watching a health panel; on a phone nobody is. The ladder in `adaptive.ts` steps down after ~6
seconds of sustained strain and climbs back after ~30 seconds of calm, five times slower than it
falls, because oscillation looks worse to an audience than sitting one rung low. Every change is
explained in one sentence on the viewfinder, and choosing a rung by hand resets it.

**Rungs are applied with `setParameters`, not by re-opening the camera.** Bitrate, resolution scale
and frame rate change inside the live connection: no renegotiation, no new peer connection, no gap.
This is what makes automatic adaptation acceptable at all — the desktop quality selector re-opens
the camera, which blacks the outgoing video out for as long as the device takes, and doing that
automatically would be worse than the stutter it fixed. The limits survive reconnects, because a
reconnect is exactly when they matter.

**Capture is 30fps and starts at 720p.** Not a cosmetic default: 60fps roughly doubles the uplink
bitrate and the heat, which are the two things a mobile broadcast has least of. And nothing
available before publishing measures the *uplink* — `navigator.connection.downlink` is a
downstream estimate — so the starting rung is the one likely to survive, with the measured ladder
free to hold it there or step down. 1080p is offered but never chosen automatically.

**Two mobile failures the desktop studio has no equivalent of.** The screen locking suspends
capture, so a wake lock is held while on air and released afterwards. And the operating system
takes the camera for a call or an app switch — iOS by *muting* the track rather than ending it, so
the WebRTC stack notices nothing and the audience watches a frozen frame. The capture layer
re-opens the camera whenever the page returns and the track is muted or ended, and says so.

**No compositor on the phone.** The studio composes a program on a canvas and mixes audio through a
Web Audio graph. On a phone that is a second encoder's worth of work, on the device least able to
spare it, for a layout nobody can build one-handed. The camera publishes directly.

**One anchor is deliberately not a `Link`.** The links between the two broadcasters do a full
document load. `/studio/[id]` does not read the query string on the server, so a client-side
navigation to `?view=desktop` is served from the router cache and the subtree never re-renders —
the address bar changes and the screen does not. This was caught by the handover e2e test, not by
reasoning.

## Verification

Everything below was run, in containers, against this working tree.

| What | Command | Result |
| --- | --- | --- |
| Unit and component tests | `npx vitest run` | **450 passed**, 23 files (52 new) |
| Types | `npx tsc --noEmit` | clean |
| Lint | `npx eslint .` | clean |
| Production build | `npx next build` | succeeded, 14 routes |
| Mobile end-to-end | `npx playwright test --project=mobile-chrome` | **3 passed** against the live stack |
| Whole end-to-end suite | `npx playwright test` (with the Phase 2 overlay) | **45 passed**, 2 skipped, 0 failed |

The end-to-end run is the one that proves the phase. It drives a Pixel-sized Chromium — touch,
coarse pointer, phone user agent, so the broadcaster is chosen by real detection rather than a test
flag — through login, session creation, capture, WHIP publish into the real gateway, and stop, then
reads the session state back from the real API and asserts the server agreed it was broadcasting.

A note for whoever runs the desktop suite next: it needs the Phase 2 overlay
(`docker-compose.e2e.yml`) and its environment variables, or eleven specs fail in ways that look
like product faults and are not — the gateway's control API, the stand-in RTMP platform and the
disposable mail server are all test-only services that the base stack does not publish.

New tests, by what they pin down:

- `tests/mobile-policy.test.ts` — form-factor detection including the iPad-reports-as-Mac case,
  starting-quality policy, and that the studio's own 60fps rungs are untouched.
- `tests/adaptive-quality.test.ts` — the ladder: ignores a single bad sample, steps down after
  sustained strain, holds after a change, never falls off the bottom, climbs back far more slowly
  than it fell, respects a pinned ceiling, and treats missing stats as no evidence rather than bad
  news.
- `tests/adaptive-controller.test.tsx` — the hook around it, including the regression it was
  written for: a new broadcast gets a new publisher at full quality, so the ladder has to start at
  the top or its first climb would *apply* a reduced rung to a healthy stream.
- `tests/mobile-studio.test.tsx` — the screen: 30fps constraints, motion content hint, publish
  before start, flip without a new credential, mute without stopping the track, and camera recovery
  from an iOS-style muted track.
- `tests/whip-publisher.test.ts` (extended) — `applyEncoding` retunes without a renegotiation, the
  limits survive a reconnect, and a publisher that was never given limits sends exactly the
  parameters it always did.

Two things were found by looking at the screen rather than by any of the above, which is the
argument for doing both: a bare **0** under the running timer where the viewer count goes, and the
studio's advice line telling somebody holding a phone to *plug into the network with a cable*.
Advice that cannot be followed teaches people to stop reading it, so the phone has its own
(`mobileAdvice`), and the studio's is untouched.

## Not in this phase

- **Background broadcasting.** A browser cannot capture from the background, and
  `docs/08-mobile-app.md` already requires that it not be assumed. The viewfinder says what
  happened when the OS interrupts.
- **App store distribution.** A PWA installs from the browser. ADR 0021 states the trade.
- **Chat.** Phase 3's scope mentions "basic chat/session status". Session status is delivered;
  chat does not exist anywhere in the platform yet, on any client, so this is not a mobile gap.
- **Production tooling on the phone** — scenes, overlays, multi-device sources, AI operations.
  Deliberate: these need a mouse and a keyboard, and the sheet links to the full studio.

## Operating it

See `docs/troubleshooting/mobile-broadcasting.md` for reaching the studio from a real phone on a
local network, why HTTPS is not optional there, and how to reproduce the interruption and
adaptation paths.

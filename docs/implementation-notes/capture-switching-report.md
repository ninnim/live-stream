# Live capture switching — implementation report

Phase 5 groundwork: changing what the studio captures, mid-broadcast, without interrupting it.

## What changed

The studio used to disable its device pickers the moment a session went live. Connecting a capture
card, moving to a different camera, or lowering resolution because the connection was struggling all
required ending the broadcast first. All three are things that happen *during* a show.

Now:

- **Cameras, microphones, capture quality and screen sharing change while live**, with no dropout.
- **Devices are grouped by how they are attached**, cable-connected hardware first.
- **Hardware that disappears is recovered from automatically**; hardware that appears is announced.
- **The studio measures its own outgoing video** and says whether a problem is the computer or the
  connection.

## How the swap works

`RTCRtpSender.replaceTrack` exchanges the media source inside the transport that is already open —
no renegotiation, no new credential, no gateway-visible change.

Two structural changes made it possible:

**The publisher owns its tracks.** `WhipPublisher` previously read them from the `MediaStream` it
was constructed with; after a device change that stream describes the past, so a later reconnect
would have quietly reverted to the camera the operator had unplugged.

**Capture is acquired one track at a time.** `requestCameraTrack` and `requestMicrophoneTrack`
replace the old paired `getUserMedia` call. The paired version is the obvious design and has a
specific defect: changing camera re-opens the microphone, which is an audible dropout on air.

A single preview `MediaStream` is created once and has its tracks exchanged in place, so the preview
never rebinds and never flashes black.

## Files

| Area | File |
|---|---|
| Capture, classification, presets | `apps/web/src/lib/media/devices.ts` |
| Quality measurement (pure) | `apps/web/src/lib/media/quality.ts` |
| Track swapping, stats, tuning | `apps/web/src/lib/media/whip.ts` |
| Capture state, hot-plug, screen share | `apps/web/src/hooks/useMediaDevices.ts` |
| Quality polling, swap reporting | `apps/web/src/hooks/useBroadcaster.ts` |
| Picker, quality, screen share | `apps/web/src/components/studio/DeviceControls.tsx` |
| Sent-vs-received read-out | `apps/web/src/components/studio/HealthPanel.tsx` |
| Phone camera switching | `apps/web/src/components/device/JoinClient.tsx` |

Rationale for every decision above is in
[ADR 0011](../decisions/0011-live-capture-switching.md). Operator-facing guidance is in
[capture-devices.md](../troubleshooting/capture-devices.md).

## Verification

| Suite | Result |
|---|---|
| Backend build | 0 warnings, 0 errors |
| Backend unit | 277 passed |
| Backend integration | 121 passed |
| Frontend lint / typecheck / build | clean |
| Frontend unit | 139 passed |
| End-to-end (real stack) | 16 passed |

The end-to-end proof is mechanical rather than visual. `e2e/device-switching.spec.ts` records the
media gateway's WebRTC **session id** before and after a switch and asserts it is unchanged — a
different id would mean the transport restarted and the audience saw a dropout. It also asserts
exactly one publisher remains on the session path, because a swap that *added* a publisher rather
than replacing one would leave two encoders contending for the same path: a fault the gateway
resolves intermittently, and in production rather than in a test.

Two synthetic cameras come from `--use-fake-device-for-media-stream=device-count=2`, so "switch to
the other camera" is a real device change rather than re-opening the same hardware.

## Two bugs the tests caught

**The `ended` listener watched the wrong thing.** It was keyed on the selected camera id, which a
screen share deliberately leaves alone — so the listener never re-bound to the screen track, and the
browser's own *Stop sharing* bar did nothing at all. It now binds to the track object held in state.
The general shape is worth remembering: *do not watch a proxy for the thing you actually care
about.*

**Recovery orphaned the dead track.** Clearing the track ref before re-opening meant the next
`adoptTrack` had no "previous" to remove, leaving two video tracks on a stream meant to carry one.
The ended track is now left in place for the adopt path to remove and stop.

Both were found by tests written for the behaviour, not by reading the code back.

## Known gaps

- **No program switching.** Choosing which contributing device the audience watches is unchanged
  from Phase 4 and still belongs with the composition pipeline.
- **One camera at a time.** A second angle comes from a paired device, not a second local camera.
- **No scenes, overlays, lower thirds, or audio mixer.** Remaining Phase 5 scope.
- ~~**Screen share carries no audio.**~~ Closed — `getDisplayMedia` now asks for it and the screen's
  sound is mixed with the microphone. See
  [ADR 0017 §8](../decisions/0017-broadcast-quality.md#8-the-game-and-the-voice-both-have-to-go-out).
- **The connection heuristic is label-based** and will mislabel unusual hardware. It degrades to
  "Other", and the device works regardless.
- **Quality is never lowered automatically.** Deliberate — see ADR 0011 §7 — but it does mean an
  unattended broadcast will not adapt its rung on its own.

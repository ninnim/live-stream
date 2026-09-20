# Phase 5 — Professional Live Studio: implementation report

**Acceptance criterion:** *"A professional user can produce a multi-source show natively from the
platform for the primary supported workflows."*

## Scope, and where each part landed

| Scope item | Status | Where |
|---|---|---|
| Scenes | Done | `session_scenes`, `ScenePanel`, keys `1`–`9` |
| Multiple sources | Done | Phase 4 pairing + the compositor |
| Camera switching | Done | `replaceTrack`, [ADR 0011](../decisions/0011-live-capture-switching.md) |
| Screen share | Done | `requestScreenTrack`, graded for legibility |
| Overlays | Done | Canvas overlays drawn after the picture |
| Lower thirds | Done | Live control; text saved with a scene |
| Branding | Done | `session_branding`, watermark and accent colour |
| Audio mixer | Done | `AudioMixer` + `AudioMixerPanel` |
| Remote guests | Done | Phase 4 pairing, previewed over WHEP |
| Hotkeys | Done | `useStudioHotkeys` |
| Preview/program | Done | Monitors per source, tally, program monitor |

Reasoning is in [ADR 0012](../decisions/0012-program-switching-and-composition.md) (switching and
composition) and [ADR 0013](../decisions/0013-scenes-overlays-and-branding.md) (the production
tools). The operator's guide is [program-switching.md](../troubleshooting/program-switching.md).

## What was added in this pass

Program switching and the compositor were the previous pass. This one added the production tools
that sit on top of them.

**Backend.** Two tables — `session_branding` (one per session, created with it) and `session_scenes`
— with `StudioService` and six endpoints under the session. The migration backfills branding for
sessions that already exist; without that, every existing session would load with no branding and
the studio would throw on open.

**Frontend.** Overlay drawing in the compositor, `useStudioConfig`, `ScenePanel`, `OverlayPanel`,
`AudioMixerPanel`, and `useStudioHotkeys`.

## Verification

| Suite | Result |
|---|---|
| Backend build | 0 warnings, 0 errors |
| Backend unit | 305 passed |
| Backend integration | 144 passed |
| Migrations | no pending model changes |
| Frontend lint / typecheck / build | clean |
| Frontend unit | 201 passed |
| End-to-end (real stack, two browsers) | 23 passed |

The end-to-end specs cover the things worth proving in a real browser: a scene surviving a reload
and rearranging the studio when recalled, branding persisting against the session, keyboard control
starting and stopping composition **without the broadcast noticing** — asserted, as everywhere in
this phase, by the media gateway's WebRTC session id being unchanged — and a keystroke typed into a
field never reaching the switcher.

## Three things the tests caught

**A two-source layout with one source composed for nothing.** Pressing `W` with a single camera
started the compositor, paid the whole cost of composition to draw one camera full frame, and showed
the operator no feedback at all — the switcher panel does not appear until there is something to
switch between. The rule is now "compose when there is something to draw that the raw camera cannot
provide", which also makes captions work on a solo shot.

**A comment that described behaviour the code did not have.** The opacity slider carried a comment
saying it committed on release; it committed on every step of the drag. Both it and the colour
picker now hold a local value and save when the operator lets go — which was also a real defect,
since a drag sent a request per step and the responses could land out of order.

**A function declared to return a boolean that returned `undefined`.** `isTypingTarget` ended in
`|| target.isContentEditable`, which is unimplemented in some environments. Harmless where it was
used, and exactly the kind of thing that reads as false in one place and is compared with `=== false`
in another.

## Known gaps

- **No transitions.** Cuts are hard cuts, and a scene recall is a cut.
- **No animation on the lower third**, and no second overlay slot — no ticker, no countdown.
- **No scene reordering** in the UI; scenes keep creation order.
- **No workspace-level branding.** A new session starts from the defaults rather than inheriting.
- **No stored audio mix.** Levels and mutes are overrides for the moment.
- **No customisable key bindings.**
- **Two sources on screen maximum.** A grid for more would be a new layout, not a new mechanism.
- **Composed mode needs the studio tab visible** — browsers throttle canvas drawing in a background
  tab. A plain solo shot is unaffected, and the panel says which mode is running.

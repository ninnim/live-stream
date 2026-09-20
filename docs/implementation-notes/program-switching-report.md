# Program switching — implementation report

Phase 5: choosing what the audience sees, and changing it mid-show without interrupting the
broadcast.

## What changed

Phase 4 gave a session several contributing cameras and a control room that could see all of them.
The audience still only ever saw the studio: `IsProgram` existed on the model, and nothing set it.

Now an operator cuts between sources from the switcher, in three layouts, without the broadcast
stopping. The studio composes the program in a canvas and publishes that, so a cut redraws the frame
rather than re-forming the connection.

## The route that was ruled out

Repointing a proxied gateway path per cut is the obvious server-side design, and it cannot be built:
adding or modifying a MediaMTX path at runtime panics the gateway and takes every live session on
the box with it. That was established in Phase 4 and is why this is a browser-side compositor.

The fallback within that family — an FFmpeg relay restarted per cut — works but stalls the output
for as long as the restart takes. Every viewer rebuffers and every downstream platform sees a gap.

Full reasoning in [ADR 0012](../decisions/0012-program-switching-and-composition.md).

## How it works

The studio pulls each contributing device over WHEP, using the read-scoped preview credential Phase 4
already issued. Those pictures are drawn into a canvas, and the canvas is what gets published.

Three properties fall out of that, and together they are the whole argument:

- **The media plane did not change.** One WHIP stream into the session path, exactly as Phase 1.
- **A cut is instantaneous.** The outgoing track never changes, so the encoder never restarts.
- **Layouts, source labels and later overlays are the same mechanism** — more drawing on a canvas
  already being drawn.

Composition is not always running. A solo studio shot publishes its camera straight through, because
`requestAnimationFrame` is throttled in a background tab and the common broadcast should keep the
behaviour it had. The boundary costs one `replaceTrack` in each direction.

## Files

| Area | File |
|---|---|
| Program endpoint and transaction | `services/api/src/LiveStream.Application/Sources/SourceService.cs` |
| Route | `services/api/src/LiveStream.Api/Endpoints/SourceEndpoints.cs` |
| Layout geometry (pure) | `apps/web/src/lib/media/layout.ts` |
| Canvas compositor | `apps/web/src/lib/media/compositor.ts` |
| Audio mixing | `apps/web/src/lib/media/audio-mixer.ts` |
| WHEP subscriber | `apps/web/src/lib/media/whep.ts` |
| Orchestration | `apps/web/src/hooks/useProgram.ts` |
| The switcher | `apps/web/src/components/studio/ProgramPanel.tsx` |

Operator guidance: [program-switching.md](../troubleshooting/program-switching.md).

## Verification

| Suite | Result |
|---|---|
| Backend build | 0 warnings, 0 errors |
| Backend unit | 277 passed |
| Backend integration | 128 passed |
| Migrations | no pending model changes |
| Frontend lint / typecheck / build | clean |
| Frontend unit | 177 passed |
| End-to-end (real stack, two browsers) | 19 passed |

The end-to-end proof is mechanical. `e2e/program-switching.spec.ts` records the media gateway's
WebRTC session id for the session path before a cut and again after it, and asserts it is unchanged
— a different id would mean the studio republished, which every viewer would have seen. It also
asserts exactly one publisher remains on the path, because a composition that *added* a publisher
rather than replacing one would leave two encoders contending for it.

Three journeys are covered: cutting to a phone, cutting back to the studio, and holding both on air
in a side-by-side layout.

## Things worth recording

**The return leg is the one that quietly breaks.** Cutting back to the studio requires the studio's
own source to be reported as `Connected`, which happens through the presence reconciler rather than
anywhere in the switching code. Nothing asserts it unless a test actually makes the round trip, so
one does.

**The mixer has to key on tracks, not streams.** `createMediaStreamSource` binds to the track a
stream holds when it is called and does not follow later changes. Keying on the stream means changing
microphone mid-show leaves the graph attached to a stopped track — silence, with nothing reporting an
error anywhere.

**An assertion that matched two things.** The first end-to-end run failed on `getByText("On air")`
matching both the tally badge and the disabled button beneath it. The fix was in the product: a
source already on air does not need a button offering to cut to it, so it no longer has one.

## Known gaps

- **No transitions.** Cuts are hard cuts.
- **No overlays, lower thirds or branding** beyond the source name drawn in multi-source layouts.
  The mechanism is there; the content is not.
- **No independent audio mixing.** `AudioMixer` supports per-source level and mute, but nothing
  exposes them. The policy is fixed: audio follows the picture, plus the studio microphone always.
- **No automatic or voice-activated switching.**
- **Composed mode needs the studio tab visible.** A background tab throttles canvas drawing. Solo
  studio shots are unaffected, and the panel says which mode is running.
- **Two sources maximum on screen.** The layouts are `solo`, `side-by-side` and
  `picture-in-picture`; a grid for more would be a new layout, not a new mechanism.

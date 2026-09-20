# ADR 0012 — Program switching and composition

**Status:** Accepted
**Date:** 2026-09-06
**Phase:** 5 — Professional Live Studio

How the platform decides what the audience sees, and how it changes that mid-show without
interrupting the broadcast.

## 1. The problem deferred from Phase 4

Phase 4 gave a session several contributing devices, and the model has always carried an `IsProgram`
flag. Nothing set it. The control room could see every camera and manage every camera, but the
audience only ever saw the studio.

[ADR 0010](0010-multi-device-contribution.md) deferred this deliberately: *"it changes where the
session's program comes from, and therefore touches Phase 1's verified media flow."* This decides
where the program comes from.

## 2. The route that was ruled out first

The obvious server-side design is a proxied path: define a `program` path in the media gateway whose
source is whichever contributing path is on air, and repoint it on each cut.

**It cannot be built.** Adding or modifying a MediaMTX path at runtime panics the gateway process
and takes every live session on the box down with it — a nil-pointer dereference in
`recordcleaner.ReloadPathConfs`, reproduced on 1.11.3 and recorded in
[ADR 0010](0010-multi-device-contribution.md) and
[the Phase 4 troubleshooting guide](../troubleshooting/phase-4-multi-device.md).

The fallback within that family — an FFmpeg relay pulling the chosen source over RTSP and
republishing into the session path — works, but a cut means killing one FFmpeg process and starting
another. The output stalls for as long as that takes, every viewer rebuffers, and every downstream
platform sees a gap. That is not a cut; it is an outage with good intentions.

## 3. What was built: compose in the browser

The studio draws the sources that are on air into a canvas and publishes **that canvas** as its
outgoing video track. Cutting changes what is drawn.

The consequences are worth stating individually, because together they are the entire argument:

- **The media plane did not change.** The studio still publishes one WHIP stream into the session
  path exactly as it did in Phase 1. No new service, no new hop, no gateway configuration.
- **A cut is instantaneous.** The outgoing track never changes, so the encoder does not restart, the
  resolution does not change, and no keyframe is requested. Viewers see frame *n* of one source and
  frame *n+1* of another.
- **Layouts, labels and — later — overlays and lower thirds are the same mechanism.** They are more
  drawing on a canvas that is already being drawn.
- **Contributing devices are pulled in over WHEP**, using the read-scoped preview credential Phase 4
  already issues. Nothing new was needed to authorize it.

The cost is a decode and re-encode in the operator's browser, and the studio tab must stay open.
Both were already true: the studio was always the publisher.

### The proof

`e2e/program-switching.spec.ts` records the media gateway's **WebRTC session id** for the session
path before a cut and again after it, and asserts it is unchanged. A different id would mean the
studio tore down and republished — which every viewer would have seen. It also asserts exactly one
publisher remains on the path, because a composition that *added* a publisher rather than replacing
one would leave two encoders contending for it.

## 4. Composition is not always running

A solo studio broadcast publishes its camera straight through, exactly as before. The canvas comes
up only when the show needs it: a second source on air, or a layout that shows more than one.

This is not an optimisation for its own sake. `requestAnimationFrame` is throttled when a tab is
backgrounded, so a composed program in a hidden tab would drop to a crawl — whereas a camera track
published directly is unaffected. Keeping the simple case simple means the common broadcast keeps
the old behaviour, including in a background tab.

The boundary costs one `replaceTrack` in each direction, which is the seamless swap already built
and verified for device switching ([ADR 0011](0011-live-capture-switching.md)).

## 5. The server records the decision; it does not move the media

`POST /sources/{id}/program` promotes one source and demotes the previous one **in a single
transaction**. A session has exactly one program, and two sources both believing they are on air is
a state the control room cannot render honestly — it would survive until someone noticed.

The endpoint deliberately does nothing to the media plane. What viewers see follows from the studio
drawing it. The flag exists so that the decision is durable, appears in the audit trail, reaches
other control rooms over the realtime channel, and can be read back by anything that needs to know
what was on air and when.

Two rules are enforced in the domain rather than the UI:

- **A source that is not `Connected` cannot be put on air.** Cutting to a source with no media is
  cutting to black, which is the one thing a switch must never do.
- **A source that carries no media at all — a moderator — is never a candidate.**

Re-cutting to whatever is already on air is a no-op, not an error: an operator pressing the button
twice should not see a failure, and the outgoing frame should not flicker.

## 6. Ordering: the program is always slot zero

Every layout puts the program in the first slot, and the first slot is the full frame in `solo`, the
background in `picture-in-picture`, and the left column in `side-by-side`.

That is a contract, not an implementation detail. It means cutting decides *what* is on air and
never *where it sits*, so an operator changing shot under time pressure has one decision to make
rather than two.

## 7. Sources are contained, not cropped

A source whose aspect ratio does not match its slot is letterboxed or pillarboxed, never
cropped-to-fill.

Cover-cropping looks better for a 16:9 camera in a 16:9 frame — where the two are identical anyway —
and is actively harmful for the case this platform is built around. A phone held upright is a
first-class source here, and filling a 16:9 slot from a 9:16 camera means showing a narrow vertical
strip of the middle of it: routinely a torso and no head. **Bars are honest; a cropped face is not.**

## 8. Audio follows the picture, and the studio is always heard

Every source on air is in the mix, plus the studio microphone unconditionally. The host is
narrating; going silent because the operator cut to a guest is not what anyone means by a cut.

The mixer's output track is created once and kept for the session, for the same reason the canvas
track is: swapping an audio track is audible, and doing it on every cut would make switching sound
broken.

The mixer keys inputs on the **track**, not the stream. `createMediaStreamSource` binds to the track
a stream holds at the moment it is called and does not follow later changes — so keying on the
stream means changing microphone mid-show leaves the graph attached to a stopped track, and the
studio goes silent with nothing reporting an error.

## 9. The monitor tiles are the compositor's inputs

There is no hidden second copy of each video. The `<video>` element the operator is watching in the
switcher is the element the compositor draws from.

One decoder per source rather than two, and — more importantly — it is structurally impossible for
what the operator is watching to drift from what the audience is getting.

## 10. What this is not

- **No transitions.** Cuts are hard cuts. A dissolve is more drawing and belongs with overlays.
- **No overlays, lower thirds or branding** beyond the source name drawn in multi-source layouts.
  The mechanism is in place; the content is not.
- **No independent audio mixing.** Levels and mutes exist in `AudioMixer` but are not exposed; the
  policy is "audio follows video, plus the studio".
- **No automatic switching.** Voice-activated or scene-based switching is Phase 6 territory.
- **Composition requires the studio tab to be open and foregrounded** while a multi-source layout is
  on air. See §4.

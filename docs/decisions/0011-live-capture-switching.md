# ADR 0011 — Live capture switching and quality

**Status:** Accepted
**Date:** 2026-09-04
**Phase:** 5 — Professional Live Studio (capture groundwork)

How the studio changes what it is capturing — camera, microphone, screen, resolution — without
interrupting a broadcast, and how it tells an operator why a stream is not smooth.

## 1. The problem this replaces

Until now the studio disabled its device pickers the moment a session went live, with the message
*"Devices cannot be changed while you are live. Stop the broadcast to switch."*

That was honest about the implementation and wrong for the product. Plugging in a capture card,
switching to the camera pointed at the guest, or dropping resolution because the connection is
struggling are all things that happen **during** a show, not before one. Requiring an operator to
end the broadcast to do any of them makes the platform unusable for the work it exists for.

## 2. `replaceTrack`, not renegotiation

WebRTC offers exactly the tool needed: `RTCRtpSender.replaceTrack` exchanges the media source inside
an already-open transport. No new offer, no new answer, no new ICE, no new credential — the gateway
does not observe a change at all beyond the pixels arriving.

The alternative, tearing down the peer connection and publishing again, is what the old lock was
avoiding. It costs the audience a visible dropout and the session a trip through `RECONNECTING`.

The end-to-end proof is deliberately mechanical: `e2e/device-switching.spec.ts` records the media
gateway's WebRTC **session id** before and after a switch and asserts it is unchanged. A different
id means the transport restarted; the same id means it did not.

### Consequences for track ownership

The publisher previously read its tracks from the `MediaStream` it was handed at construction. That
had to change: after a device switch, that stream describes the past. `WhipPublisher` now owns
`videoTrack` and `audioTrack` and re-adds *those* on every reconnect, so a connection that drops
five minutes after a camera change comes back on the camera the operator actually chose.

## 3. One track at a time

Capture is acquired per track — `requestCameraTrack`, `requestMicrophoneTrack` — rather than as a
camera-and-microphone pair.

The paired call is the obvious design and it has a specific defect: changing camera re-opens the
microphone, and re-opening a live microphone is an audible dropout for everyone watching. Splitting
the calls means a camera change touches only video.

## 4. One preview stream, swapped in place

The preview `MediaStream` is created once and its tracks are exchanged inside it. Creating a new
stream per switch would force the preview element to rebind and flash black — which, during a live
show, is indistinguishable from a fault to the person operating it.

## 5. Cable, wireless, built-in, virtual

No web API reports how a device is attached. The picker groups devices anyway, inferring the
connection from label conventions — a USB `vendor:product` pair, `Bluetooth`, `Built-in`,
`OBS Virtual Camera` — and ordering **Connected by cable** first.

This is a heuristic and is documented as one. It earns its place on two grounds: the distinction is
the one users actually reason about ("use the camera I just plugged in"), and being wrong is
cosmetic — the grouping changes, the device still works. Anything unrecognised is reported as
`unknown` rather than guessed at.

Order of evaluation matters more than the patterns themselves. A label can satisfy several
("USB Bluetooth Headset"), so the more specific claim wins. Phone-as-webcam bridges — Camo, EpocCam,
DroidCam — classify as `virtual` rather than `wired`, because which mode they are running in is
precisely what we cannot see.

## 6. Hardware that disappears

A track fires `ended` when its device goes away: a USB camera unplugged, an interface pulled, the
browser's own *Stop sharing* bar pressed. That event is the earliest and most reliable signal
available, so it — not device-list polling — drives recovery. The studio falls back to any remaining
device of that kind and says so.

A device **appearing** is announced but never selected. Changing the shot because someone plugged in
a dock would be a far worse failure than making them click once.

The listener binds to the track object held in state, not to a derived value. An earlier version
keyed it on the selected camera id, which silently failed for screen sharing — a screen share
deliberately leaves the camera selection alone, so the listener never re-bound and the *Stop
sharing* bar did nothing. The bug is recorded here because the shape of it recurs: **do not watch a
proxy for the thing you actually care about.**

## 7. Smoothness: whose fault is it?

A stuttering broadcast has two common causes that need opposite responses:

| Cause | `qualityLimitationReason` | What helps | What wastes the operator's time |
|---|---|---|---|
| Encoder cannot keep up | `cpu` | Close applications, lower quality | Anything about the network |
| Uplink cannot carry it | `bandwidth` | Lower quality, use a cable | "Close other applications" |

Only the browser can tell these apart, which is why publish-side measurement exists alongside the
server's health. The server sees what *arrived*; the studio sees what was *sent*. Showing both, side
by side, makes the gap between them legible — and that gap is the network.

Thresholds are deliberately generous (5% loss for "poor", 2% for "strained"). A false alarm during a
normal blip teaches operators to ignore the indicator, which is worse than not having one.

**The quality rung is never lowered automatically.** A resolution drop mid-sentence is
indistinguishable from a fault to everyone watching, and the operator is the one who knows whether
the shot matters more than the smoothness. `nextLowerQuality` exists to *offer* the step down.

## 8. Encoder tuning

Camera video is published with `degradationPreference: "maintain-framerate"`: under load, shed
resolution and keep motion fluid. A soft but smooth picture reads as live television; a sharp,
juddering one reads as broken.

Screen content is graded the other way — `maintain-resolution`, selected by a `contentHint` of
`detail` — because text that blurs is unreadable, whereas a slide arriving a frame late is not.

Tuning is applied best-effort. `setParameters` support is uneven, and its absence costs a little
smoothness under load rather than breaking the broadcast; failing a publish over a hint would trade
a working stream for a cosmetic one.

## 9. Audio processing is off by default

> **Superseded** by [ADR 0017 §11](0017-broadcast-quality.md#11-voice-enhancement-was-a-checkbox-nobody-could-answer).
> The toggle is now a **Sound type** choice — Speech or Music — and Speech is the default. The
> reasoning below rested on a claim about what processing does to a track that measurement did not
> support.

`echoCancellation`, `noiseSuppression` and `autoGainControl` are tuned for conference calls, where
they are a clear win. On a broadcast they are a liability: noise suppression audibly mangles music,
and automatic gain control pumps the level of anyone using a real microphone.

They are exposed as a single **Voice enhancement** toggle, off by default, for the genuine case of
someone talking into a laptop in an untreated room.

## 10. Screen sharing

Screen share is a video source, not a separate mode: it swaps the outgoing video track exactly like
a camera change, so it lands mid-broadcast without a dropout, and the camera is released while it
runs so its indicator light goes out.

There are two ways out of a share — the studio's *Stop sharing* button and the browser's own bar —
and both converge on the same `ended` path, so they cannot diverge.

## 11. What this is not

This is capture-side work. It does **not** deliver:

- **Program switching** between multiple contributing devices. Choosing which *source* the audience
  watches still belongs to the composition pipeline, for the reasons in
  [ADR 0010](0010-multi-device-contribution.md) §Program switching.
- **Scenes, overlays, or an audio mixer.** Still Phase 5 scope.
- **Multiple simultaneous local cameras.** One camera and one microphone go out at a time; a second
  angle comes from a paired device.

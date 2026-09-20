# ADR 0016 — A camera and a microphone are both optional

**Status:** Accepted
**Date:** 2026-09-06
**Supersedes part of:** [ADR 0011 — live capture switching](0011-live-capture-switching.md)

## The defect this fixes

The studio's way in opened the camera first and, if that failed, stopped:

```ts
await openCamera();      // throws "No camera was found."
await openMicrophone();  // never reached
```

So a desktop with no webcam could not enter the studio at all. It was shown a message telling it to
connect a camera, and that was the end of the session.

The frustrating part is that screen sharing was already built, tested, and able to carry a whole
broadcast — but the button lived *inside* the studio, which could only be reached after a camera had
already succeeded. A working feature behind a gate that demanded the one thing the machine did not
have.

## The rule

**One working capture device is enough.** A machine with no webcam is an ordinary machine, and a
platform that refuses to let it broadcast has decided that owning a webcam is a requirement — which
it is not, because a screen and a voice are a broadcast.

What the studio does require is *something*: a session started with no tracks would sit in STARTING
until the server timed it out, which reads as a platform fault rather than as a machine with no
devices. So **Start Live** is disabled, visibly, rather than the control disappearing.

| The machine has | The studio does |
|---|---|
| Camera and microphone | Opens both |
| Microphone only | Opens on sound, and offers a screen to give it a picture |
| Camera only | Opens, and says the broadcast will have no sound |
| Neither | Offers **Share a screen instead** from the entry prompt |

## Consequences worth stating

**Both entry paths are offered together.** Screen sharing is not a thing you find after a camera has
succeeded; it is one of the two ways in, and on some machines it is the only one.

**A missing device is a notice, not an error.** Nothing is broken, and there is something useful to
do about it — so it is rendered as a prompt with the action attached, not as a red banner.

**Ending a screen share can leave no video.** Previously it always returned to the camera. With no
camera to return to, the video track is dropped and the broadcast continues with sound. The same
applies to a microphone unplugged with no second one behind it.

**A device that exists but will not open is retried once.** The commonest cause is another page still
letting go of it — a reloaded studio racing its own previous instance. Accepting that as "no
microphone" silently dropped the sound for the rest of the broadcast, and, because the resume path
then republished without audio, cost a reloaded session its recovery entirely. A refused *permission*
is never retried: the answer will not change, and asking again is a second prompt for somebody who
already said no.

**A device acquired after going live triggers a brief reconnect.** There is no transceiver to swap it
into, and WHIP offers no way to add one in place. The alternative is a device the studio shows as
live and the audience never hears, which is the worse failure by a distance.

That renegotiation happens **only once the connection is established**. Doing it while a connection
is still being made tears down the attempt in flight — which is exactly what a studio reload does,
since it acquires devices and resumes publishing at the same moment. A device added during that
narrow window is picked up by the next reconnect instead.

## What this does not change

The media path. The publisher already added whatever tracks the stream had and skipped what it did
not, so a video-only or audio-only broadcast was always possible at the transport level — it was the
studio that would not let you have one.

## Addendum — screen capture is graded for what it carries

Screen sharing was captured at 30 fps and always tagged `contentHint = "detail"`, which tells WebRTC
to **keep resolution and shed frame rate** under load. That is right for slides and code — text that
blurs is unreadable — and wrong for anything that moves, where 900p at a steady 60 beats a
razor-sharp stutter.

Rather than pick a compromise that serves neither, the share carries a mode:

| Mode | Frame rate | Content hint | Sheds first |
|---|---|---|---|
| Presentation (default) | 30 | `detail` | Frame rate |
| Gameplay | 60 | `motion` | Resolution |

**The default stays 30/presentation.** 60 fps costs roughly twice the bitrate, and an unknown share
is more likely to be a document than a game — so the more expensive choice is made deliberately.

**Resolution is never constrained.** A shared display arrives at whatever size it is, and the encoder
scales it down under load according to the mode. Constraining it at capture would throw away detail
the connection might well have had room for.

**Frame rate is `ideal`, never `exact`.** A display that cannot manage 60 shares at whatever it does
rather than failing the request outright.

**Changing mode mid-share re-constrains the live track** rather than re-capturing it. Re-capturing
would put the screen picker back in front of somebody who is broadcasting. Where a browser refuses
`applyConstraints`, the setting is kept and the operator is told that sharing again applies it —
silently keeping the old frame rate under a new label would be worse than saying so.

### A defect this exposed

Changing the **Quality** dropdown while sharing a screen re-opened the camera, taking the share off
air mid-broadcast. Those rungs are camera resolutions and a shared display has none. The control now
carries the screen setting while sharing, and the camera rung is remembered for when the camera
comes back.

### Still not captured: system audio

> **Superseded** by [ADR 0017 §8](0017-broadcast-quality.md#8-the-game-and-the-voice-both-have-to-go-out).
> `getDisplayMedia` now asks for audio, and the screen's sound is mixed with the microphone.

`getDisplayMedia` is called with `audio: false`, so a game's sound never reaches the broadcast — only
the microphone does. Capturing it means mixing two audio sources into the single track the publisher
sends, which the Phase 5 audio mixer can do but nothing currently asks it to. Named here rather than
left for someone to discover mid-stream.

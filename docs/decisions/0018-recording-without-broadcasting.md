# ADR 0018 — Recording without broadcasting

**Status:** Accepted
**Date:** 2026-09-12
**Builds on:** [ADR 0017 — broadcast quality](0017-broadcast-quality.md)

Not every session is a show. A walkthrough, a bug report, a lesson, a rehearsal — all of them want
the studio's screen share, its microphone handling and its preview, and none of them want an
audience. Until now the only way to get a recording out of this platform was to go live.

## 1. The recording is written to the operator's own computer

The alternative was a *record-only session*: publish to the gateway as usual, never make it
watchable, never start a destination, and let the existing recording pipeline produce the file.
That reuses more and fits the platform's shape better — the recording would be listed, retained and
access-controlled like every other one.

It was not chosen, and the reason is bandwidth. Recording a screen is the case where **not
uploading** is the whole point: it works on a poor connection, it works with no connection, and it
starts the instant it is pressed. A server-side record-only mode would have spent a 6 Mbps upload
to produce a file the operator then had to download again.

The consequence is stated plainly in the studio: nothing is uploaded, nobody can watch it, and it is
not in the platform's recordings list. It is a file, and it is theirs.

## 2. A `MediaRecorder` must never be handed the studio's stream

The single most important line in this change, and the least obvious.

The studio's preview stream is created once and its **tracks are exchanged inside it** — that is the
design that lets a camera change, a screen share, or a microphone swap land mid-broadcast without a
dropout (ADR 0011). The `MediaRecorder` specification says this about a stream whose track set
changes while recording:

> the UA MUST immediately stop gathering data, **discard any data that it has gathered**, and queue
> a task to fire an error event named `InvalidModificationError`.

Not stop. Not truncate. *Discard.* An hour of recording would vanish because somebody changed
camera.

So the recorder is given **its own `MediaStream`**, and the API takes **tracks, not a stream** — so
there is no stream for a caller to hand over and then mutate. The track set it starts with is the
track set it keeps.

## 3. Audio goes through the mixer; video does not

Isolating the stream solves the catastrophic failure. It does not stop a *source* from ending
underneath the recording, and the two kinds want opposite answers.

**Audio is routed through the Phase 5 mixer.** Its output track is stable for the mixer's whole
life, so the sources feeding it can change without the recorder noticing. This is not theoretical:
sharing a screen with its sound adds a source to the mix mid-recording (ADR 0017 §8), and a
microphone can be swapped at any time.

**Video is the source track itself.** Making it survive a source change would mean drawing it
through a canvas, and that costs the recording its resolution and frame rate — which, for a screen
recording, is the entire reason for making one. The existing compositor is capped at 720p30 for
exactly the right reasons on the program path, and exactly the wrong ones here.

So a video source that ends **stops the recording and keeps every byte written so far**, and says
why. Losing the file silently would be the worst outcome this feature could have.

## 4. Where the bytes go

| Browser | Sink | Consequence |
|---|---|---|
| Chrome, Edge | `showSaveFilePicker` → written straight to the chosen file | Memory stays flat however long it runs |
| Everything else | Held in memory, delivered as a download on stop | Fine for minutes; not for an hour of 1080p |

The in-memory limit is **said while recording rather than discovered afterwards**: the failure mode
is a tab running out of memory an hour into something unrepeatable.

The save dialog is opened from the click on **Start recording**, because a file picker not opened
from a user gesture is refused outright. Dismissing it is treated as a decision, not a fault — no
error, no banner. The cost of that choice is that a browser which *refuses* the picker for some
other reason is indistinguishable from a cancel; headless Chromium is one, rejecting with the same
`AbortError` a person pressing Cancel produces. Treating a refusal as a cancel is the safer way
round: a spurious error banner on every deliberate cancel would be worse.

## 5. Container, and quality

Preference order is **MP4**, then VP9 WebM, then VP8 WebM. MP4 wins wherever it is offered: it opens
in the editors and phone galleries a recording actually ends up in, where a `.webm` is a file people
have to go and find a player for. Chrome only gained MP4 recording recently, so WebM stays a real
fallback rather than a formality. A session with no picture at all gets an audio container, because
wrapping a microphone in a video container produces a file that plays as a black rectangle.

Bitrates are **12 Mbps video and 192 kbps audio** — higher than the broadcast settings on purpose.
A local recording has no network to fit inside, so sizing it like a stream would throw away quality
for nothing.

Chunks are handed over every second and written as they arrive. That is what keeps memory flat on
the streaming sink, and it bounds what is lost if the browser is killed outright.

## 6. It is not a mode

Recording sits beside **Start Live**, not in place of it. Deciding "is this session a broadcast or a
recording?" before opening the studio would force a choice before the operator knows the answer, and
recording a show while it goes out is a perfectly reasonable thing to want. Starting or stopping a
recording touches nothing in the publish path.

## 7. What this does not do

**The recording is not the platform's.** It is not listed, not retained, not shareable, and not
subject to workspace retention policy. That is the direct cost of not uploading it, and it is the
right trade for the case this exists for — but a workspace that needs recordings kept and governed
should go live with recording enabled, which already does all of that.

**A browser crash loses the in-memory recording**, and loses the un-flushed second of a streamed
one. The page warns before unload while recording, which is the most a page is allowed to do.

# ADR 0020 — Hardware detection that does something

**Status:** Accepted
**Date:** 2026-09-20
**Builds on:** [ADR 0011 §7](0011-live-capture-switching.md), [ADR 0017](0017-broadcast-quality.md)

The studio already detected whether a GPU was doing the encoding and **did nothing with the
answer**. A laptop encoding 1080p60 in software was told so in a metric, and left to discover for
itself that the frame rate collapses once the fans spin up. This is the acting half.

## 1. Where automatic adaptation belongs, and where it does not

ADR 0011 §7 says the quality rung is **never lowered automatically**: a resolution drop mid-sentence
is indistinguishable from a fault to everyone watching, and the operator is the one who knows
whether the shot matters more than the smoothness. That decision stands and is not touched here.

But it is a decision about **mid-broadcast** adaptation. It says nothing about the starting point,
and that is precisely where hardware detection belongs:

> Choosing a rung the machine can sustain, **before anyone is watching**, costs nothing and
> surprises nobody.

So this runs once, on the way into the studio, and never again.

## 2. `MediaCapabilities.encodingInfo`, asked as `webrtc`

The standard API for "can this machine encode this configuration". Asked per codec, per rung, at
the bitrates the publisher would actually send — asking about 1080p60 at a trivial bitrate would
get an answer about a broadcast nobody is going to make.

Asked as `type: "webrtc"`, not `record`: a browser can record a configuration it cannot send in
real time, and the answers differ.

## 3. `powerEfficient` decides, not `smooth`

Measured in a container with no GPU at all, Chrome reports **`smooth: true` for 1080p60 software
encoding** across H.264, VP9 and VP8 alike. `smooth` is an optimistic claim that the codec can run,
not a load test of this machine. Believing it would start every laptop at the rung most likely to
cook it.

`powerEfficient` means hardware-accelerated. That is a fact rather than a prediction, and it is the
fact that decides whether a machine holds 1080p60 or overheats.

| What was found | Where the studio starts |
|---|---|
| A GPU that encodes the rung | That rung, at its frame rate |
| No GPU, ≥ 12 cores | 1080p, **30 fps** |
| No GPU, ≥ 6 cores | 720p, 30 fps |
| No GPU, fewer | 540p, 30 fps |
| Browser will not say | The platform default, unchanged |

Software 1080p **60** is never recommended. It is where a machine without a hardware encoder falls
over, and it falls over live rather than in a settings panel.

The last row matters as much as the others: inventing a worse default from no evidence would make
this a downgrade for everybody whose browser lacks the API.

## 4. Two rules that keep it from being annoying

**The operator always wins.** Once somebody has picked a rung or a frame rate, the recommendation
stops applying — the probe is asynchronous, and one that landed a moment after a person chose
1080p and quietly moved them back would be worse than no recommendation at all. It also stops
applying the moment a camera is open: by then it has missed its window.

**It says what it found.** The settings visibly moved, and somebody who does not know why would
reasonably conclude the studio had a mind of its own. One sentence under the pickers, naming the
evidence: *"No hardware encoder, but 24 CPU cores — 1080p at 30 fps is a safe start."*

## 5. Codec preference follows the hardware

`preferredVideoCodecs` ranked H.264 first, always (ADR 0017 §3). That is right when H.264 is
hardware-accelerated and wrong when it is not: some Intel and AMD parts accelerate VP9 and not
H.264, and keeping H.264 first there hands the broadcaster's CPU a job their GPU was willing to do.

A hardware codec is now promoted above H.264 **only when H.264 itself is not accelerated**. The
cost is a transcode at the relay — which has its own hardware encoder (ADR 0017 §5) — paid to save
the broadcaster's CPU, which is the scarcer of the two and the one whose exhaustion the audience
actually sees. With no evidence either way, the order is exactly as it was.

## 6. What this does not do

**It does not adapt while live.** Deliberately — see §1. `nextLowerQuality` still exists to
*offer* the step down, and the health panel still reports `qualityLimitationReason: cpu` when the
encoder cannot keep up.

**`hardwareConcurrency` is a weak proxy.** It counts logical cores, not how fast they are or what
else is using them, and browsers are free to under-report it for privacy. It is only consulted when
there is no hardware encoder at all — at which point it is the last honest signal available.

**It cannot see the relay's hardware.** That is probed separately and by trial encode, because the
two run on different machines and the browser's answer says nothing about the server's.

# ADR 0017 — Broadcast quality: audio, frame rate, and encoder selection

**Status:** Accepted
**Date:** 2026-09-07
**Builds on:** [ADR 0016 — optional capture devices](0016-optional-capture-devices.md)

The platform was configured throughout for a video call. This is the change to broadcast settings,
and the reasoning for each number.

## 1. Audio was mono speech, and nothing said so

The largest single quality defect, and the least visible. The WHIP offer went out untouched, so
Opus was negotiated with the browser's defaults: **mono, around 32 kbps**, tuned for intelligible
speech over a poor connection. Correct for a conference call. Audibly wrong for anything with music,
a game, or two people talking at once.

There is no track constraint or sender parameter for this. Opus's channel count and target rate are
decided by the `fmtp` line in the SDP, which means **an untouched offer is a decision** — just not a
deliberate one.

The offer now asks for `stereo=1; sprop-stereo=1; useinbandfec=1; maxaveragebitrate=128000`.

- Both `stereo` and `sprop-stereo`: MediaMTX echoes the parameters it is offered, and a mismatch
  silently downgrades the session to mono.
- Whatever the browser already asked for is kept — `minptime` and anything a future version adds —
  rather than replacing the line wholesale.
- An offer with no Opus is returned exactly as it was. A broadcast that publishes with default audio
  is far better than one that fails because a regular expression did not match.

Capture asks for **48 kHz stereo** explicitly, and the relay now sends AAC at **160 kbps, 48 kHz**.
The old 44.1 kHz was a resample away from what Opus carries natively, spending CPU to throw away the
top of the band because 44.1 is what CDs used. 160 rather than 128 because this is a *second* lossy
encode of already-lossy audio, and generation loss is where a broadcast starts sounding like a phone
call.

## 2. Video was capped by a default nobody chose

WebRTC picks its own ceiling for a screen share — around 2.5 Mbps — which is thin at 1080p and
visibly thin at 1080p60. The sender now carries a **6 Mbps ceiling**, and the relay encodes to the
same figure.

It is a ceiling, not a target: WebRTC sends far less for a still slide and backs off on its own when
the connection cannot carry it, and x264 in ABR mode spends nothing extra on a static picture.

## 3. The browser was publishing VP8

Left to itself a browser negotiates **VP8**, and the gateway confirmed it: `tracks: Opus, VP8`.
It is the worst available choice on three counts at once.

- **Practically no GPU has a VP8 encoder**, so VP8 is encoded on the CPU. That is the whole
  reason the health panel read `CPU`, and on a laptop it is the difference between holding
  1080p60 and watching the frame rate collapse as the fans spin up.
- **It is the oldest codec in the list and looks it**, most visibly on the high-detail,
  high-motion content — games — that costs the most to send.
- **RTMP carries H.264 only**, so every other choice guarantees a transcode at the relay.

The video transceiver now asks for H.264 first, then VP9, then VP8, before the offer is
created. Verified against the running gateway: `tracks: Opus, H264`, and the relay's own
banner now reads `Video: h264 (Constrained Baseline)` where it read `vp8`.

Every codec the browser offered is **kept**, only reordered. Dropping one would be a way to
make a session fail to negotiate at all on some future browser; reordering only changes what
is chosen when there is a choice. And every failure in this path is silent —
`setCodecPreferences` is unevenly implemented and a browser may offer no H.264 at all, in
which case the session negotiates exactly what it would have negotiated anyway. Losing a
codec preference costs quality; throwing would cost the broadcast.

Camera rungs moved to 60 fps at 1080p and 720p for the same reason — `ideal`, so a 30 fps
camera is unaffected. 540p stays at 30: it exists for a struggling connection, and doubling
its frame rate would work against the reason somebody picked it.

## 4. Frame rate: 30, 60, or 120 — and what actually survives

Screen capture offers three rates. What matters more than offering 120 is being honest about it:

| Stage | Ceiling |
|---|---|
| `getDisplayMedia` capture | 120 |
| Browser WebRTC encode | **60** |
| RTMP ingest at YouTube, Facebook, Twitch | **60** |

So 120 is captured and then dropped. It is offered because a 120 Hz display should be able to send
what it has, and it is **labelled "capture only" in the picker** with a note beneath the preview
saying where the frames go. Offering it silently would be a lie told once per session.

The relay's forced output rate rose from 30 to 60 to match. It stays *forced* rather than passed
through, for the reason in ADR 0016: a WebRTC source has no frame rate of its own.

## 5. Encoder selection must be a trial, not a capability list

The obvious implementation is `ffmpeg -encoders`, and it is wrong. The stock Debian build reports
`h264_nvenc`, `h264_qsv` and `h264_vaapi` **on a machine with no GPU at all** — they are compiled in,
not available. Selecting on that evidence fails at the first frame:

```
[h264_nvenc @ …] Cannot load libcuda.so.1
Error initializing output stream 0:0
```

...which happens after the broadcast has started, on the operator's stream rather than at startup.

So each candidate **encodes a fraction of a second of blank video to nothing**, and what survives is
what the hardware really offers. Probed once at startup, logged, and reused: hardware does not appear
while the service runs, and probing per relay would put a subprocess in the path of every broadcast.

Preference order is NVENC, Quick Sync, then libx264. Hardware first — it frees the CPU the rest of
the relay competes for, and at these bitrates the quality difference against x264 `veryfast` is not
the deciding factor.

**Encoders are not interchangeable on the command line**, so each carries its own arguments.
`-preset veryfast` is an x264 preset and means nothing to NVENC, which numbers p1–p7; Quick Sync
encodes from NV12 rather than YUV420P. Keeping the arguments beside the encoder name is what stops a
wrong flag being discovered live.

**VAAPI is deliberately absent.** It encodes from hardware frames, so it needs its own upload filter
chain rather than the scale filter everything else shares. Half-supporting it would mean an encoder
that probes clean and then fails on the first real frame.

An operator naming an encoder overrides the probe — they may know something it cannot — but the
named one is still trialled, so a wrong choice is reported at startup rather than mid-broadcast.

### The container has no GPU by default, and `--gpus all` is not enough

On a stock `docker compose up`, the probe correctly selects libx264: the relay container is given no
GPU at all. That much is expected. What is not obvious is the next step.

**`--gpus all` grants `compute,utility`, and NVENC is in neither.** On a machine with a working GPU,
`nvidia-smi` runs inside the container, `libcuda.so.1` is present, CUDA works — and NVENC still
fails:

```
[h264_nvenc @ …] Cannot load libnvidia-encode.so.1
[h264_nvenc @ …] The minimum required Nvidia driver for nvenc is 470.57.02 or newer
```

The second line is a red herring; the driver is fine. The encode libraries live behind a separate
driver capability, `video`, which has to be asked for:

```yaml
environment:
  NVIDIA_DRIVER_CAPABILITIES: video,compute,utility
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: all
          capabilities: [gpu, video]
```

That is `infrastructure/docker/docker-compose.nvidia.yml`. It is an **overlay rather than part of
the base file** because a device reservation is a hard requirement — compose refuses to start the
service on a machine that cannot satisfy it, and the base stack has to come up on a laptop with no
GPU.

Verified on an RTX 4060 through Docker Desktop on WSL2: the probe selects NVENC at startup, and a
real broadcast's encoder banner reads `h264 (native) -> h264 (h264_nvenc)`.

Intel and AMD need `devices: [/dev/dri:/dev/dri]` instead, and note that WSL2 does not expose
`/dev/dri` for video encode — Quick Sync and VAAPI need a Linux host.

## 6. The studio reports which encoder *your* machine is using

Separately from the relay: Chrome exposes `encoderImplementation` in `getStats()`, and the studio was
discarding it. It is the only signal a page gets about whether the GPU is doing the work, and it is
the difference between a laptop that holds 1080p60 and one whose fans spin up and whose frame rate
collapses.

The health panel shows **GPU**, **CPU**, or **Not reported**, with the browser's own name for the
encoder beneath it.

**It reads `powerEfficientEncoder`, not the encoder's name.** That is a standard boolean, defined to
mean hardware-accelerated, that Chrome reports on every outbound video stream — and the first
version of this panel ignored it, matching on `encoderImplementation` strings instead. That was
wrong, and wrong in the way that matters: hardware encoder names are not stable, they differ per
platform and change between browser versions, so a machine whose GPU was plainly doing the work was
told its encoder was **Unknown**. Name matching survives only as a fallback for browsers that omit
the boolean, and only for the *software* names, which are stable.

"Not reported" replaced "Unknown" for the same reason. A browser declining to answer is a different
thing from a machine having no GPU, and the old word read as the latter.

## 7. Defaults, and why each one

| Setting | Value | Reason |
|---|---|---|
| Opus bitrate | 128 kbps stereo | Browser default is mono ~32 kbps |
| Capture sample rate | 48 kHz stereo | What Opus carries natively; no resample anywhere |
| AAC egress | 160 kbps, 48 kHz | Second lossy encode; margin against generation loss |
| WebRTC video ceiling | 6 Mbps | Sized for 1080p60; a ceiling, not a target |
| Relay video bitrate | 6000 kbps | Same, and 2500 was visibly blocky in motion |
| Relay frame rate | 60 | Matches the browser and platform ceiling |
| Relay max frame | 1920×1080 | Platform limit; larger is accepted and never shown |
| Relay encoder | `auto` | Trialled at startup, strongest first |
| Screen capture default | 30 fps, graded for detail | An unknown share is more likely a document than a game |
| Screen sound level | 0.7 against the mic's 1.0 | A game at unity buries the player, inaudibly to them |
| Missing-track synthesis | On | A stream missing a kind is hidden by the platform, not refused |
| Sound type | Speech | Almost every broadcast is a person talking in an untreated room |

Every one is overridable per deployment. The screen capture default stays at 30 because 60 costs
roughly twice the bitrate and the expensive choice should be made deliberately.

## 8. The game and the voice both have to go out

A broadcast of a game is two sounds — what the game is playing and what the player is saying — and
the publisher sends exactly **one** audio track. So they cannot both be attached to it. They have to
be summed first.

`getDisplayMedia` is now called with `audio: true`. That is what puts "Share system audio" (whole
screen) or "Also share tab audio" (a tab) in front of the person sharing. Whether they tick it is
theirs to decide and **most of the time they will not**, so the track is optional at every point
downstream — and when it is absent the studio says, once, how to include it. Firefox and Safari
do not offer the checkbox at all; the share still works, without its sound.

### The mixer is built only when there is something to mix

| Open | Published |
|---|---|
| Microphone only | The microphone track itself |
| Screen sound only | The screen track itself |
| Both | A Web Audio mix of the two |
| Neither | Nothing, and the sender stops |

A graph in the path costs CPU, adds a buffer of latency, and adds a component the browser can
suspend into perfect silence with no error anywhere. None of that is worth paying for a mix of one.

Screen sound starts at **0.7** against the microphone's 1.0. Mixed at unity a game buries the person
playing it, and — this is the part that matters — the broadcaster cannot hear that it is happening,
because they are listening to the game on their own speakers rather than to the mix. A fader for
each source is offered whenever there are two, and only then.

### Three things this had to get right

**The published track survives a level change.** Moving a fader sets a gain inside a graph whose
output track stays the same object. Swapping the published track is audible, and a slider moves a
lot.

**`ended` is watched on the devices, not on what is published.** While mixing, the published track
is a Web Audio destination, and a destination never fires `ended` — there is no device behind it to
lose. Watching it would mean a microphone could be unplugged mid-broadcast with nothing noticing.

**Muting mutes the microphone, not the mix.** A broadcaster stepping away should silence themselves
and leave the game playing. Disabling the microphone track feeds the mixer silence, so the mix keeps
running and nothing is re-published.

### What this fixes on the way past

A machine with **no microphone** sharing a screen with sound now publishes a real audio track. That
is the commonest shape of the §9 problem solved at the source rather than at the relay, which is the
better place for it: real game audio beats generated silence.

## 9. A stream missing a kind of media is hidden, not rejected

Facebook Live requires an audio track. A video-only stream is **accepted, counted as live, and never
shown to anybody** — no error, no warning, nothing in any log to find afterwards. The same is true
of an audio-only stream everywhere. This is the failure that cost the most time in this whole
effort, precisely because it announces nothing.

So the relay generates the kind that is missing.

| Source carries | Sent to the platform |
|---|---|
| Video and audio | Exactly what arrived — unchanged, no extra input, no mapping |
| Video only | The broadcast's picture, plus generated digital silence |
| Audio only | The broadcast's sound, plus a black 720p picture |
| Could not tell | Exactly what arrived |

### The last row is the important one

The relay establishes what the source carries with **ffprobe**, before it builds its encoder
arguments — RTSP answers a DESCRIBE with an SDP listing the tracks, so a publishing source answers
in well under a second and no media has to be read.

A probe that cannot answer must change nothing. Generating audio over a source that *has* audio
would replace a broadcaster's voice with silence for a whole show — far worse than the failure being
fixed — so "I could not tell" is a distinct answer from "there is none", and an empty ffprobe result
counts as the former. That asymmetry is what the type models and what most of the tests are about.

It is established **per encoder run, not once per relay**: a broadcaster who reconnects having
plugged in a microphone changes the answer, and a relay recovering from that restart must not keep
generating silence over sound that has just appeared.

### Three things that would otherwise have gone wrong

**`-shortest` is not optional.** A generated input never ends. Without it the encoder keeps running
after the broadcaster has stopped, holding the destination open and pushing silence — or a black
frame — at the platform indefinitely. The end-to-end spec asserts the platform's tracks go away
when the broadcast does, which is what makes that real rather than assumed.

**`copy` cannot copy a stream that does not exist.** With `VideoCodec: copy`, a synthesised picture
still has to be encoded, or the relay fails at startup on a configuration that works for every
other broadcast.

**ffprobe has no `-nostdin`.** That is an ffmpeg option. Passing it makes ffprobe consume the next
argument as its value and exit 1 — which looks exactly like a source that could not be inspected,
so the whole feature silently did nothing. Caught by the end-to-end spec on its first run, not by
reading the code.

The black picture is generated at **10 fps** and normalised up by the output rate. It exists to
satisfy a platform, not to be looked at, and sixty byte-identical 1.3 MB raw frames a second is
real CPU spent producing nothing.

## 10. The broadcaster could not hear their own microphone

Everything the studio said about audio described what was **asked for**: a device selected, a track
unmuted, a mode chosen. A microphone can be all three and stone silent — muted on the device itself,
docked on the wrong input, a gain knob at zero — and the health panel would cheerfully report
`Microphone: On` throughout. The broadcaster found out when somebody watching told them.

There is now a **level meter**, and it is the single most useful control in the studio for anyone who
has ever been told "we cannot hear you".

| Reading | Meaning | What it says to do |
|---|---|---|
| **No sound** | Below −55 dBFS RMS | Check it is not muted on the device itself |
| **Too quiet** | Below −34 dBFS RMS | Move closer, or raise the system input level |
| **Good** | In between | Nothing |
| **Too loud** | Peak at or above −1 dBFS | Move back, or lower the input level |

Loud is judged on **peak** and the rest on **RMS**, because they are different faults. Clipping is a
momentary sample hitting the ceiling; "too quiet" is a sustained average nobody can hear. One number
would either miss the transient or call every pause between words silent.

It says nothing at all while the level is fine. A meter that explains itself when nothing is wrong
trains people to stop reading it.

### It taps the signal; it is never in the path

An `AnalyserNode` is a leaf — it reads what is connected to it and outputs nothing. So a fault in the
meter cannot make the broadcast quieter, louder, or silent; the worst it can do is show a wrong
number. That is deliberate, and it is why the meter does not reuse the mixer's graph.

It measures the **devices**, not what is published. While the screen's sound is being mixed in, the
published track is a Web Audio destination, and a meter on that could not tell the broadcaster
*which* of their two sources had gone quiet.

A muted microphone reads **Not open**, not silence. Muting is a deliberate act; telling somebody who
has just muted themselves to go and check their device is not muted would be absurd.

## 11. "Voice enhancement" was a checkbox nobody could answer

The old control was a checkbox labelled *Voice enhancement*, **off** by default. Two things were
wrong with it.

**The default was wrong, and for a reason that turned out not to be true.** The code comment
justifying it claimed the browser's processing "forces mono at 16 kHz". Measured in the browser the
studio actually runs in, a processed track reports **48 kHz and two channels** — the same as an
unprocessed one. The processing does not downgrade the capture. (Measured against Chromium's
synthetic device, so the sample rate is the fake device's; what it establishes is that processing
does not force the downgrade the comment claimed.)

With the stated reason gone, the default is wrong on the merits. Almost every broadcast is a person
talking, usually into a laptop microphone in an untreated room, and echo cancellation, noise
suppression and automatic gain control are what stand between them and sounding like a bad phone
call. Somebody broadcasting music **knows they are** and will go looking for the setting. Somebody
talking does not know their room is the problem.

**The question was unanswerable as posed.** "Voice enhancement: on or off" asks about a mechanism.
The control is now **Sound type**, and asks about content:

| Sound type | Processing | For |
|---|---|---|
| **Speech** (default) | Echo cancellation, noise suppression, auto gain | A person talking |
| **Music** | None | Instruments, singing, a mixer feed |

48 kHz stereo is requested in both. The mode decides what is done to the signal, never what is
captured.

Changing it **re-opens the device**, because these are capture constraints applied when a track is
created — `applyConstraints` on a live audio track does not re-engage the processing chain. That is
a brief gap in sound for anyone watching, which is why it is a named choice rather than something to
fiddle with while hunting for the right setting.

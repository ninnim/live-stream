# Cameras, microphones and capture quality

Everything about getting video *into* the studio: connecting hardware by cable or wirelessly,
changing it mid-show, and diagnosing a stream that is not smooth.

## Connecting a camera

### By cable

Anything the operating system exposes as a camera works, with no driver and no configuration:

- **USB webcams** — Logitech, Razer, Insta360 and similar.
- **HDMI capture cards** — Elgato Cam Link, AVerMedia, Magewell. A mirrorless camera or a games
  console plugged into one appears as an ordinary camera.
- **USB microphones and audio interfaces** — Blue Yeti, Focusrite Scarlett, Rode.
- **A phone over USB**, through a bridge app (Camo, EpocCam, DroidCam). These present as *virtual*
  cameras, so that is how the picker labels them — it cannot see whether the phone is on a cable or
  on Wi-Fi.

The picker groups devices by how they appear to be attached, with **Connected by cable** first.
Plug something in while the studio is open and a banner announces it. It is never selected for you —
changing the shot because someone plugged in a dock would be worse than one extra click.

> The grouping is inferred from the device's name, because no browser API reports the transport. A
> device it cannot place lands under **Other** and works exactly the same.

### Wirelessly

Two different things go by "wireless" here.

**A Bluetooth microphone or headset** is just another device in the picker, under **Wireless**. Be
aware that Bluetooth audio adds latency and that many headsets drop to narrowband quality when their
microphone is in use — a cabled microphone is materially better for broadcast.

**A phone as a camera** does not use Bluetooth at all. It joins over the network as a paired device:
control room → **Add device** → scan the QR code. See
[phase-4-multi-device.md](phase-4-multi-device.md). A phone joined this way is a full contributor
with its own connection to the gateway, not a webcam plugged into the studio machine.

### Wired network vs Wi-Fi

For the machine running the studio, this is the single highest-value change you can make. Wi-Fi
upload is bursty and shares airtime with everything else in the building; a live broadcast needs
sustained, even upload far more than it needs peak speed. If the quality read-out says
**Your upload connection cannot carry this quality**, reach for a cable before anything else.

## Changing devices while live

**Cameras, microphones, quality and screen sharing can all be changed during a broadcast.** The
studio swaps the media source inside the connection it already has open, so the audience sees a cut
rather than a dropout, and the session never leaves the air.

That includes hardware connected *after* the show started — plug in a capture card mid-show and it
appears in the picker ready to use.

If a device is unplugged while it is on air, the studio falls back to another one automatically and
tells you it did. Nothing needs to be restarted.

## Quality

| Setting | Use it when |
|---|---|
| **1080p** | Wired network, a machine with headroom, detail matters |
| **720p** | The default. Reliable on domestic upload, looks right everywhere |
| **540p** | Upload is limited, the machine is struggling, or you are on mobile data |
| **Automatic** | Let the browser choose — reasonable for an unfamiliar webcam |

Resolutions are requested, not demanded, so a camera that tops out below the rung you picked gives
you what it has rather than an error. **Sending** shows what you are actually getting, which is
regularly not what was asked for — a capture card negotiating 1080p when you asked for 720p is
normal and fine.

## Making the microphone sound good

### Watch the meter, not the button

Beneath the device pickers is a live meter showing what your microphone is **actually picking up**.
Everything else — the picker, the mute button, the health panel's "Microphone: On" — tells you what
was *asked for*. A microphone can be selected, unmuted, and stone silent.

| It says | What it means | What to do |
|---|---|---|
| **Good** | Your level is right | Nothing |
| **Too quiet** | You can be heard, barely | Move closer, or raise the input level in your system sound settings |
| **Too loud** | You are distorting | Move back, or lower the input level |
| **No sound** | Nothing is arriving at all | Check the microphone is not muted on the device itself — many headsets have their own switch |
| **Not open** | You muted it here, or there is no microphone | Nothing, if you meant to |

Speak normally before going live and watch it sit in **Good**. That is the whole check.

### Sound type

| Sound type | What it does | Use it for |
|---|---|---|
| **Speech** (default) | Removes room echo and background noise, and evens out your level | A person talking — almost every broadcast |
| **Music** | Nothing at all. Full range, nothing removed, nothing levelled | Instruments, singing, a mixer or interface feed |

Leave it on **Speech** unless you are broadcasting music. Noise suppression is what saves a laptop
microphone in a normal room; it is also what mangles a guitar, which is why the other option exists.

Changing it re-opens the microphone, so there is a brief gap in sound. Set it before you go live.

Both modes capture at 48 kHz stereo — the mode changes what is *done* to the sound, never what is
captured.

## The Encoder reading, and what it means

While broadcasting, the health panel reports whether **your machine's GPU** is doing the video
encoding. It is the difference between holding 1080p60 and watching the frame rate collapse as the
fans spin up.

| It says | Meaning |
|---|---|
| **GPU** | Hardware encoding. Nothing to do |
| **CPU** | Software encoding — expect higher CPU use and a lower ceiling |
| **Not reported** | This browser declines to say. **Not** the same as having no GPU |

This is read from `powerEfficientEncoder`, the browser's own answer, so it does not depend on
recognising an encoder by name. Safari and older Chrome versions do not report it at all, which is
what **Not reported** means — the encoder's name is still shown beneath, and is often enough to tell.

If it says **CPU** on a machine with a capable GPU, the usual cause is the codec. Practically no GPU
has a VP8 encoder, so a session that negotiated VP8 is encoded on the CPU no matter what hardware is
present. The studio asks for H.264 first for exactly this reason; a browser that offers no H.264 will
still fall back to VP8.

This reading is about **your** machine. The relay that sends to YouTube or Facebook has a separate
encoder running in Docker — see
[phase-2-distribution.md](phase-2-distribution.md#the-relay-says-it-selected-libx264-but-this-machine-has-a-gpu).

### The studio picks a starting quality for your machine

On the way in, the studio asks the browser what this machine can actually encode, and starts you
somewhere it can hold. It says what it found in one line under the pickers:

| What it found | Where it starts you |
|---|---|
| A GPU that encodes the rung | That rung, at its frame rate |
| No GPU, but plenty of CPU cores | 1080p at **30** fps |
| No GPU, a modest machine | 720p at 30 fps |
| No GPU, a weak machine | 540p at 30 fps |
| A browser that will not say | The usual default, unchanged |

**It never overrules you.** Pick a rung or a frame rate and it stops applying — and it only ever
applies before a camera is open. It also never changes anything mid-broadcast: a resolution drop
mid-sentence looks like a fault to everyone watching, so stepping down while live stays your call.

Software **1080p60** is never chosen for you. It is where a machine without a hardware encoder
falls over, and it falls over live rather than in this panel. Set it yourself if you know your
machine handles it.

## Reading the quality panel

While broadcasting, the health panel shows both what the server received and what this browser sent.

| Reading | Meaning |
|---|---|
| **Outgoing video: Smooth** | Nothing to do |
| **Outgoing video: Strained** | Working, but something is limiting it — read the banner |
| **Outgoing video: Poor** | Heavy loss, or frames are not going out |
| **Upload** vs **Received** | A large gap between them is the network between you and the server |

The banner names the cause, because the two common ones need opposite responses:

- **"This computer cannot encode video fast enough."** Your machine, not your connection. Close
  other applications — screen recorders and video calls are the usual culprits — or drop to 540p.
  Adding bandwidth will not help.
- **"Your upload connection cannot carry this quality."** Your connection, not your machine. Drop
  the quality, or move to a cable. Closing applications will not help.

Quality is never lowered for you. A resolution drop mid-sentence looks like a fault to everyone
watching, and only you know whether the shot matters more than the smoothness.

## Common problems

### The camera picker is empty, or a device is missing

- Labels only appear after permission is granted. Before that, devices are listed positionally.
- A camera held by another application will not open — close video calls and recorders.
- On Windows, check **Settings → Privacy → Camera** allows desktop apps.
- Reconnect the device; the list refreshes on its own.

### "Your camera is being used by another application"

Exactly what it says. The commonest culprits are a video call in another tab, a recorder, or a
virtual-camera app holding the device exclusively.

### Screen sharing does not appear as an option

`getDisplayMedia` needs a desktop browser and a secure context. On a phone the button will report
that the browser cannot share a screen.

### Video freezes after a device change

This should not happen — the swap is reported as an error rather than left to diverge. If the studio
shows a banner saying it could not switch, the previous source is still on air. Select the device
again, and if it persists, check the browser console.

### Everything is smooth locally but viewers see stuttering

Compare **Upload** against **Received** in the health panel. If Upload is healthy and Received is
not, the problem is between this machine and the server: check the network path, and prefer a cable.

## Sharing a screen, and 60 fps

While a screen is shared, the **Quality** control is replaced by **Screen content**, because
quality rungs are camera resolutions and a shared display has none — it arrives at whatever
size it is.

| Screen content | Frame rate | Under load it sheds |
|---|---|---|
| Slides and code (default) | 30 fps | Frame rate — the picture stays sharp |
| Games and video | 60 fps | Resolution — the motion stays smooth |

Pick **Games and video** for gameplay. 60 fps costs roughly twice the bitrate, which is why
it is a choice rather than the default. Changing it mid-share re-constrains the track in
place, so the screen picker does not reappear; if your browser refuses, the studio says so
and sharing again applies it.

## Game and system sound

Your game and your voice both go out, summed into the single audio track a broadcast carries.
**You have to ask for the game's sound when you start the share** — the browser will not add it
afterwards.

| Browser | What to tick, and where |
|---|---|
| Chrome, Edge — whole screen | **Share system audio**, bottom-left of the picker |
| Chrome, Edge — a tab | **Also share tab audio**, bottom-left of the picker |
| Chrome, Edge — a window | Not offered. Share the whole screen or the tab instead |
| Firefox, Safari | Not offered at all. The share works; its sound does not come with it |

If the box was not ticked, the studio says so under the controls. Press **Stop sharing**, then
**Share screen** again, and tick it this time.

Once both are open, an **Audio balance** control appears with a fader for each. Screen sound
starts at 70% against your microphone's 100%, because a game mixed at full level buries the
person playing it — and you cannot hear that happening, since you are listening to the game on
your own speakers rather than to what viewers get. Moving a fader takes effect immediately and
does not interrupt the broadcast.

### Muting while a game is playing

The **Microphone** button mutes you, not the broadcast. The game stays audible. That is the point:
stepping away from the mic should not take the sound off air.

The **Audio balance** control has its own meter for the screen sound, next to your microphone's, so
you can see which of the two is carrying and set the balance by eye rather than by guesswork.

### If the game is silent

- **Nothing appeared under the controls, and there is no balance control.** The sound is not being
  captured. Share again and tick the box.
- **The balance control is there but viewers hear nothing from the game.** The application is
  probably playing to a different output device than the one the browser is capturing. Windows
  captures the default playback device; move the game to it in Settings → Sound → Volume mixer.
- **Only some tabs are audible.** Tab audio captures that tab. A second tab, or an application
  outside the browser, needs a whole-screen share.

See [ADR 0017](../decisions/0017-broadcast-quality.md#8-the-game-and-the-voice-both-have-to-go-out).

## Broadcasting without a webcam

A camera is optional, and so is a microphone. What the studio requires is **one** of them:
something has to be going out, or the session sits in STARTING until the server times it out.

| What the machine has | What happens |
|---|---|
| Camera and microphone | Both open. The usual case |
| Microphone only | The studio opens on sound, and offers **Share a screen** to give it a picture |
| Camera only | The studio opens, and says the broadcast will have no sound |
| Neither | The entry prompt still offers **Share a screen instead**, which is the whole broadcast |

Ending a screen share returns to the camera when there is one. When there is not, the video
track is dropped and the broadcast carries on with sound — that is not an error, and it is not
reported as one.

On a machine with **no microphone**, a screen share with its sound ticked supplies the audio track
on its own — real game audio rather than the digital silence the relay would otherwise generate to
keep the platform happy. A stream with no audio at all still reaches the platform: Facebook Live
accepts one, counts it as live, and never shows it, so the relay adds a silent track rather than let
that happen. See
[phase-2-distribution.md](phase-2-distribution.md#the-destination-says-live-bytes-are-flowing-and-the-platform-shows-nothing).

A device the machine *has* but that will not open is retried once before the studio concludes
it is absent. The usual cause is another tab, or a page that has not finished letting go —
a reloaded studio racing its own previous instance for the microphone.

**A device acquired after going live triggers a brief reconnect.** There is no transceiver to
swap it into, and WHIP cannot add one in place. The alternative would be a microphone the
studio shows as live and the audience never hears.

## Recording without going live

Sometimes you just want the file. **Record to this computer** sits above the program panel and works
without ever pressing Start Live.

1. Open the studio and set up what you want to record — camera, microphone, or **Share screen**.
2. Press **Start recording**.
3. Chrome and Edge ask where to save. Other browsers save to your downloads when you stop.
4. Press **Stop recording**.

Nothing is uploaded and nobody can watch it. The file is yours, named after the session and the
time — `product-launch-2026-09-12-1430.mp4` — so a folder of them sorts into order.

You can record while you are live, too. The two are independent: starting or stopping a recording
never touches the broadcast.

### Set your sources up first

Changing **camera or screen** while recording **ends the recording**. Everything already written is
saved and the studio says why, but it stops — the video is recorded at the source's own resolution
and frame rate, and there is no way to swap that underneath a file being written without ruining it.

Changing **microphone**, or starting a screen share **with its sound**, is fine mid-recording. Audio
goes through the mixer, so the recording does not notice.

### If you are not using Chrome or Edge

Your browser holds the recording in memory until you press Stop, and the studio says so while it is
running. Keep those recordings short — an hour of 1080p will exhaust the tab. Chrome and Edge write
straight to the file you picked and have no such limit.

### The file

| | |
|---|---|
| Format | MP4 where the browser supports it, otherwise WebM |
| Video | 12 Mbps — higher than the broadcast, because there is no network to fit inside |
| Audio | 192 kbps |
| Audio-only | Recorded as `.m4a` when there is no camera and no screen |

Do not close the tab while recording — the browser will warn you. The file is not finished until you
press Stop.

See [ADR 0018](../decisions/0018-recording-without-broadcasting.md).

## Testing without hardware

Chromium can synthesise cameras, which is how the end-to-end suite runs:

```bash
# One synthetic camera and a tone generator
--use-fake-device-for-media-stream
--use-fake-ui-for-media-stream

# Two cameras, for exercising a device switch
--use-fake-device-for-media-stream=device-count=2
```

`getUserMedia` only works in a secure context, so the studio must be on `https://` or
`http://localhost`. A LAN address such as `http://192.168.1.20:3000` will load the page and then
refuse the camera.

## Running the checks

```bash
cd apps/web && npm run lint && npm run typecheck && npm test

# Device switching against a running stack, with real WebRTC
npm run test:e2e -- e2e/device-switching.spec.ts
```

The end-to-end specs prove the swap is seamless by recording the media gateway's WebRTC session id
before and after a change: the same id means the transport never restarted.

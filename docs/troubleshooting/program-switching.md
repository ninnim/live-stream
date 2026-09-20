# Switching between sources

How to run a multi-camera show from the control room, and what to check when it misbehaves.

## Putting a second camera on air

1. **Start Live** in the studio.
2. **Add device**, name it, and have the phone scan the code —
   see [phase-4-multi-device.md](phase-4-multi-device.md).
3. On the phone: **Enable camera and microphone**, then **Start sending video**.
4. The **Program** panel appears in the studio as soon as there is more than one source. Each source
   gets a monitor.
5. Press **Cut** under a monitor to put it on air. The red **On air** badge moves to it.

Cutting does not interrupt the broadcast. The studio composes the picture locally and publishes the
result, so a cut redraws the frame rather than re-forming the connection — viewers see a cut, not a
stall.

## Layouts

| Layout | Shows | Where the program sits |
|---|---|---|
| **Full screen** | 1 source | The whole frame |
| **Side by side** | 2 sources | Left column |
| **Picture in picture** | 2 sources | Full frame, with the second inset bottom-right |

The source on air is always in the first position, whatever the layout. Cutting decides *what* is on
air, never where it appears.

A source whose shape does not match its slot is fitted with bars rather than cropped. A phone held
upright in a 16:9 frame gets black at the sides — deliberate, because cropping it to fill would show
a vertical strip of the middle, which is usually a torso and no head.

## What "Composing in the studio" means

The Program panel says which mode you are in.

**"Sending your camera directly"** — a solo studio shot. The camera goes out untouched, exactly as it
did before the switcher existed.

**"Composing in the studio. Cuts are instant."** — the canvas is running. This starts automatically
when a source other than the studio is on air, or when you pick a multi-source layout, and stops
when you return to a solo studio shot.

There is one thing to know about composed mode: **it needs the studio tab visible.** Browsers throttle
canvas drawing in a background tab, so a composed program will slow down if you switch away. A solo
studio shot is unaffected. If you need to leave the tab during a long show, cut back to the studio
first.

## Common problems

### The Program panel does not appear

It only appears once a session has more than one media source. A device that has been invited but
has not paired is not a source yet. Check the device list.

### A monitor shows "No picture" or stays on "Connecting"

The studio pulls each device's picture over WebRTC using a short-lived read credential.

```bash
# Is the device actually publishing?
curl -s http://localhost:9997/v3/paths/list | jq '.items[] | {name, ready, tracks}'

# Did the studio's preview request succeed?
docker compose logs api | grep -i "preview"
```

- **"Waiting"** means no subscription has been attempted yet — normal for a second or so.
- **"Reconnecting"** means the preview dropped and is retrying. The device may be on a poor network.
- **"No picture"** after several attempts usually means WebRTC cannot reach the studio. Previews use
  the same transport as publishing, so if the device can publish, the path exists — check that
  `8189/udp` is reachable from the studio machine too.

A failed preview never affects the broadcast. It only means that source cannot be put on air, since
the studio has nothing to draw.

### "Cut" is disabled

The button only works for a source the server reports as **Connected**. A source that is paired but
not sending cannot go on air — cutting to it would cut to black.

```bash
docker compose exec -T postgres psql -U livestream -d livestream \
  -c "select display_name, status, is_program from session_sources order by created_at;"
```

### The cut worked in the control room but viewers still see the old source

Check that the studio is composing — the Program panel says so. If it says "Sending your camera
directly" while a phone is marked on air, the studio and the server disagree; reload the studio,
which re-reads the program from the server.

### Two sources both show "On air"

They should not. The promotion and demotion are committed together, so this means two control rooms
raced and one is showing a stale view. It resolves on the next poll, within five seconds.

### The audience hears the guest but not the host, or vice versa

Audio follows the picture, **plus the studio microphone always**. Every source on air is audible,
and the host is audible regardless of what is on screen.

If a source is on air and silent, check that it is actually sending audio — a phone that granted
camera but not microphone permission will show a picture and no sound.

### The program looks choppy in composed mode

Composing costs a decode and re-encode in the operator's browser.

- Check the **Outgoing video** read-out. "This computer cannot encode video fast enough" is the
  compositor competing with everything else; close other applications or drop the quality.
- Keep the studio tab visible — see above.
- 720p is the default and the right choice while composing. 1080p doubles the work for a picture
  most viewers will not see the difference in.

## A warning that has not gone away

**Never add or modify MediaMTX path configuration at runtime.** The config API panics the gateway on
1.11.3 and takes every live session with it. Composition happens in the browser precisely so that
nothing here ever needs to touch path configuration —
see [ADR 0012](../decisions/0012-program-switching-and-composition.md) §2.

## Running the checks

```bash
cd services/api && dotnet test LiveStream.sln
cd apps/web && npm run lint && npm run typecheck && npm test

# Cutting between two real browsers against a running stack
npm run test:e2e -- e2e/program-switching.spec.ts
```

The end-to-end specs prove a cut is seamless by recording the media gateway's WebRTC session id
before and after: the same id means the transport never restarted.

# Streaming a game — from a phone, a console, or OBS

## Why this is not just "share your screen"

The mobile broadcaster publishes your **camera**. It cannot publish your screen, and no amount of
work on it will: recording one app from inside another needs an operating-system API that only a
native app can use, so no browser on any phone can capture a game (ADR 0022).

What captures a game is an encoder. You already have one, or can install a free one in a minute:

| What you are streaming | Use |
| --- | --- |
| A game on your phone | A screen-capture streaming app — Larix Broadcaster, PRISM Live, Streamlabs |
| A console | A capture card into OBS on a computer |
| Anything on a desktop | OBS, Streamlabs, vMix, or any encoder with a custom RTMP output |

They all work the same way: you give them a **server** and a **stream key**, and they publish into
your live session. Everything downstream is unchanged — the viewer page, recording, and your
YouTube or Facebook destinations all behave exactly as they do for a camera broadcast.

## Doing it

1. Open the session. On a phone, tap the **signal bars** at the top right, then find
   **Stream a game or your screen**. On a desktop, the panel is **Stream from an app or encoder**.
2. **Create a stream key.** The server address is shown in the clear; the key is hidden until you
   tap **Show**, because this panel tends to get opened by somebody who is already on camera.
3. **Copy** each value into your encoder. Most encoders have two fields and join them with a slash,
   which is why the key starts with your session's path — pasting the two halves as given produces
   the right URL.
4. Start the encoder.
5. Press **Go live**. The session goes live when your video actually arrives, not when you press
   the button.

Press **Go live** *after* the encoder is running, or within the first minute: a session that is
started and sees no video for sixty seconds is treated as a failed start.

## The key

- **Shown once.** The server keeps only a hash, so there is no way to read it back. If you lose it,
  rotate it.
- **Rotating replaces it immediately.** That is also how you make a key harmless after pasting it
  somewhere it should not be — into a chat window, or on stream.
- **Twelve hours, and it dies with the session.** Ending the session kills the key whatever its
  remaining life says.
- **Revoke** stops encoders without touching browser broadcasting, so you can cut off an encoder
  while still live from the studio.

## Reaching the server

`rtmp://localhost:1935` is the development default, and **localhost on a phone is the phone**. For
a phone on your network, set the address the encoder can actually reach:

```bash
# .env
PUBLIC_RTMP_URL=rtmp://192.168.1.20:1935
PUBLIC_SRT_URL=srt://192.168.1.20:8890
```

Unlike the studio itself, an encoder does **not** need HTTPS — the secure-context rule is a browser
rule about camera access, and an encoder is not a browser. A phone can therefore stream a game to a
plain LAN address even where the studio page needs a tunnel for its camera.

## SRT, if the signal is poor

The panel also shows an SRT URL. It carries the same credentials inside its stream id and holds a
lossy mobile uplink together far better than RTMP — worth switching to if you are streaming from
cellular and the picture keeps breaking up. Larix and OBS 29+ both support it.

## Common problems

### The encoder says it connected, and nothing goes live

Check the session, not the encoder. **`ffmpeg` and several phone apps exit cleanly when the server
rejects them**, so "no error" is not evidence that the key was accepted. If the session stays
READY, the publish was refused.

```bash
docker compose logs mediamtx | grep -i rtmp
# "is publishing to path 'ls_...'"          -> accepted
# "authentication failed: server replied 401" -> the key was refused
```

### "authentication failed" with a key you just copied

In order of likelihood:

- **The session was not prepared.** Creating a key prepares it for you; if the session has since
  ended, the key is dead. Create a new session.
- **Only half the key was pasted.** The key includes everything from the path onwards, `?user=…` and
  `pass=…` included. Encoders that split at `?` need the single-field **full URL** instead.
- **It was rotated.** Creating a key anywhere — including on another device — invalidated the old
  one.
- **The session has ended.**

### The session goes live and then fails a minute later

The encoder stopped, or never really started. A live session whose ingest disappears enters its
recovery window and fails when the window expires — the encoder's own reconnect usually beats it.

### The picture is there but the audio is missing

Most phone capture apps default to microphone-only, or to no audio at all. Game audio capture on
Android needs `MediaProjection` audio (Android 10+) and the app's own setting for it; on iOS,
ReplayKit captures app audio when the app permits it. This is an encoder setting, not a platform
one.

### I want to stream a game *and* be on camera

Start the encoder for the game, then join the same session as a second device from the phone's
camera (Phase 4 device contribution), and switch between them in the studio's program panel.

## Turning it off

External ingest is off unless configured. Unset `PUBLIC_RTMP_URL` / `PUBLIC_SRT_URL` and set
`rtmp: no` / `srt: no` in `infrastructure/mediamtx/mediamtx.yml`. The panel then tells anybody who
looks that the deployment does not accept encoders, and no key can be issued.

## Running the end-to-end tests for this

They use FFmpeg as a stand-in encoder and skip when it is absent:

```bash
docker run --rm --network host -v C:/live-stream-platform:/src \
  -v livestream-web-modules:/src/apps/web/node_modules -w /src/apps/web \
  mcr.microsoft.com/playwright:v1.62.1-noble \
  sh -c "apt-get update -qq && apt-get install -y -qq ffmpeg && npx playwright test encoder-ingest"
```

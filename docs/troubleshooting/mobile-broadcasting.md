# Phase 3 — Mobile broadcasting: setup and troubleshooting

## Going live from a phone

1. Open the studio on the phone and sign in. The mobile broadcaster is chosen automatically.
2. **Allow access** — one prompt, camera and microphone.
3. **Go live**. The timer starts when the *server* confirms your video has arrived, not when the
   button is tapped.
4. Flip, mute, or turn the picture off while filming. None of these interrupt the broadcast.
5. The bars at the top right are the connection; tapping them opens quality, the measured numbers,
   and your destinations.

Add the studio to the home screen to lose the browser chrome — the manifest is already served. It
is an improvement, not a requirement; everything works in the browser tab.

## The setting that catches everyone

**`getUserMedia` only works in a secure context**, and `http://192.168.1.20:3000` is not one. A
phone will load the studio, sign in, show the session and leave **Go live** disabled forever,
because there is no camera to broadcast. Only `https://` and `http://localhost` qualify.

For real device testing, put a tunnel in front of the studio and point the stack at it:

```bash
# .env
WEB_ORIGIN=https://your-tunnel.example.com
PUBLIC_API_URL=https://your-api-tunnel.example.com
JOIN_LINK_BASE_URL=https://your-tunnel.example.com
```

`cloudflared tunnel --url http://localhost:3000` is the quickest route. The same constraint governs
Phase 4 device pairing — see `docs/troubleshooting/phase-4-multi-device.md`, which hits it from the
other direction.

## Forcing a view

Detection is a convenience, not a verdict. Append `?view=mobile` or `?view=desktop` to the studio
URL and the choice sticks for the browser. Both studios link to the other, and both drive the same
session — the desktop studio's **Phone view**, and the mobile sheet's **Open the full studio**.

This is also how the phone broadcaster is exercised on a desktop: `?view=mobile` with the browser
narrowed, or device emulation in developer tools.

## Common problems

### The picture is frozen for viewers, and fine in the viewfinder

The operating system took the camera — a call, an app switch, the screen locking. On iOS the track
is *muted* rather than ended, so nothing in the WebRTC stack notices and the last frame keeps going
out. The broadcaster re-opens the camera when the page becomes visible again, and the viewfinder
says **Camera paused by your phone**.

If it stays frozen after coming back, the re-open failed — usually another app still holding the
camera. Check the browser console for a `NotReadableError`.

### The quality dropped and nobody touched it

That is the ladder (ADR 0021). It steps down after about six seconds of sustained strain and says
what it did, in one sentence, on the viewfinder. It climbs back after about thirty seconds of calm.

To see which of the two causes triggered it, open the sheet: a CPU limit and a bandwidth limit need
opposite responses, and the wording differs accordingly. To pin a rung, choose one by hand — that
resets the ladder and re-opens the camera at what you asked for.

### The screen keeps locking mid-broadcast

The wake lock was refused or is unsupported (Safari has it from 16.4). Nothing breaks except that
the phone locks and capture suspends — the interruption path above then applies. There is no
workaround in the browser; set a longer auto-lock on the device.

### The phone got the full studio instead

It is wider than 900 CSS pixels, or it reports a fine pointer. Tablets get the full studio
deliberately. Use `?view=mobile`.

### Going live works on a desktop and not on the phone

Check in this order:

- **Secure context** — the cause of most of these. See above.
- **Permissions** — a refused camera on iOS is re-granted in Settings → Safari → Camera, not from
  the page.
- **Session state** — a session that has ended cannot be broadcast to. The dashboard shows it.
- **Reachability** — the phone must reach both the web origin *and* the API origin. An API on
  `localhost` in the browser bundle resolves to the phone.

```bash
docker compose logs api | grep -i "ingest\|credential"
```

## Reproducing the adaptive path deliberately

The ladder responds to measurements, so the honest way to test it is to make the measurements bad:

- Chrome developer tools → Network conditions → a throttling profile, applied to the phone over
  remote debugging. WebRTC ignores the HTTP throttle, so use the next one for real effect.
- `tc` on the host, or a router-side rate limit on the phone's address, which WebRTC does feel.
- Or run the unit tests, which drive the same decision function directly and far faster:

```bash
docker run --rm -v C:/live-stream-platform:/src -v livestream-web-modules:/src/apps/web/node_modules \
  -w /src/apps/web node:22-alpine npx vitest run tests/adaptive-quality.test.ts
```

## Running the mobile end-to-end journey

Needs the stack up (`docs/troubleshooting/phase-1-local-setup.md`), and `--network host` so the
studio is reached as `localhost` — any other hostname is not a secure context and the run fails at
**Go live** being disabled.

```bash
docker run --rm --network host -v C:/live-stream-platform:/src \
  -v livestream-web-modules:/src/apps/web/node_modules -w /src/apps/web \
  mcr.microsoft.com/playwright:v1.62.1-noble npx playwright test --project=mobile-chrome
```

Note that Chromium's synthetic camera publishes below the "poor" bitrate threshold, so a healthy
session can legitimately report **DEGRADED**. Assert on "still broadcasting", never strictly on
LIVE — the same trap as the Phase 2 rig.

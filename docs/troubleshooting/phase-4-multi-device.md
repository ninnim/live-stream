# Phase 4 — Multi-device: setup and troubleshooting

## Adding a device to a session

1. Open the studio and press **Start Live** — devices need the session holding an ingest path open
   before they can publish.
2. **Add device**, choose a role, name it.
3. Scan the QR code with the phone, or open the join link and type the code.
4. On the phone: **Enable camera and microphone**, then **Start sending video**.
5. The device appears in the control room as **Sending** within a few seconds.

Removing a device is immediate: its credentials are erased and whatever it is publishing is severed
at the gateway.

## The setting that catches everyone

`JOIN_LINK_BASE_URL` decides where the QR code points. **It must be reachable from the phone.**

The default is `http://localhost:3000`, which on a phone resolves to the phone itself — the QR
scans, the browser opens, and nothing loads. Set it to an address on your network:

```bash
# infrastructure/docker/.env
JOIN_LINK_BASE_URL=http://192.168.1.20:3000
WEB_ORIGIN=http://192.168.1.20:3000
```

There is a second problem behind that one: **`getUserMedia` only works in a secure context**, and
`http://192.168.1.20:3000` is not one. A phone will load the join page and pair successfully, but
the camera button will do nothing. For real device testing you need HTTPS — a tunnel
(`cloudflared`, `ngrok`) in front of the studio is the quickest route, and both `JOIN_LINK_BASE_URL`
and `WEB_ORIGIN` must then be the HTTPS address.

## Common problems

### The pairing code is rejected

Every failure returns the same message on purpose — telling expired, spent and never-issued apart
would confirm which guesses had been real. Check in this order:

- **Already used.** Codes are single use. Issue another.
- **Expired.** Ten minutes by default (`Sources__PairingCodeLifetimeSeconds`).
- **Session ended.** A finished session accepts no new devices.
- **Rate limited.** Ten attempts per minute per address. A 429 here is the control working.

```bash
docker compose logs api | grep -i "Pairing rejected\|Device paired"
```

### The device paired but "Start sending video" does nothing

Almost always the secure-context problem above. Check the phone's browser console for a
`getUserMedia` error, and confirm the page is on `https://` or `http://localhost`.

### The device says it is sending, the control room says it is not

Presence is observed from the media plane, so this means media is not arriving.

```bash
# Find the source's path
docker compose exec -T postgres psql -U livestream -d livestream \
  -c "select display_name, media_path_name, status from session_sources order by created_at desc limit 5;"

# Ask the gateway whether anything is publishing to it
curl -s http://localhost:9997/v3/paths/list | jq '.items[] | {name, ready, tracks}'
```

- A device can only publish while the session `ExpectsIngest` — READY or broadcasting. Into a DRAFT
  session it will be refused with `LIVE_002_SESSION_NOT_READY`.
- WebRTC needs `8189/udp` reachable. On a phone over mobile data this usually needs TURN, which is
  configuration rather than code — see [ADR 0008](../decisions/0008-nat-traversal-and-studio-resume.md).

### A removed device is still streaming

It should not be. Revocation kicks the publisher at the gateway:

```bash
docker compose logs api | grep "Source revoked"
# Source revoked session=... source=... publisherKicked=True
```

`publisherKicked=False` is normal when the device had not started sending. If it is `False` while
video is still arriving, the gateway control API was unreachable at that moment — the device cannot
obtain a new credential either way, so it stops within the credential lifetime (5 minutes).

### A device disconnects and reconnects constantly

Check the presence grace period. A phone on a marginal connection may be flapping either side of it:

```bash
Sources__SourcePresenceGraceSeconds=20   # raise for poor networks
```

### `LIVE_027_SOURCE_LIMIT_REACHED`

Eight sources per session by default, including the studio. Removed devices free their slot but keep
their audit row.

## A gateway warning worth repeating

**Never add or modify MediaMTX path configuration at runtime.** On 1.11.3 the config API panics and
takes the whole media plane down with it:

```
panic: runtime error: invalid memory address or nil pointer dereference
github.com/bluenviron/mediamtx/internal/recordcleaner.(*Cleaner).ReloadPathConfs(...)
```

Every live session dies. Paths in this system are dynamic by design — the catch-all in
`mediamtx.yml` handles them and authorization runs per publish — so there is no reason to touch path
config at all.

## Running the checks

```bash
cd services/api && dotnet test LiveStream.sln
cd apps/web && npm run lint && npm run typecheck && npm test

# End-to-end, including two browsers pairing a device — needs a running stack
npm run test:e2e
```

The multi-device end-to-end specs open a second browser context as the phone, so they exercise the
real pairing flow with real WebRTC media. See
[phase-2-distribution.md](phase-2-distribution.md#running-the-end-to-end-specs) for how to run them
from a container when Node is not installed locally.

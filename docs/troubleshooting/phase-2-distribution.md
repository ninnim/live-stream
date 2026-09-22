# Phase 2 — Multi-platform distribution: setup and troubleshooting

## Extra configuration

Two new secrets are required. The stack refuses to start without them.

```bash
openssl rand -hex 32      # -> RELAY_SECRET
openssl rand -base64 32   # -> SECRETS_KEY   (must decode to exactly 32 bytes)
```

`SECRETS_KEY` encrypts every stored stream key and OAuth token. **Losing it makes them permanently
unreadable.** Back it up, and use a real secret store in production.

## Adding a destination

Any platform works with a stream key, with no app registration and no API access:

1. Open the studio for a session.
2. **Add destination**, choose the platform.
3. Paste the stream key from the platform's own dashboard.
4. Press **Start Live**. Every enabled destination starts with the broadcast.

| Platform | Where the key comes from |
|---|---|
| YouTube | YouTube Studio → Go Live → Stream |
| Facebook | Live Producer → Streaming software |
| Twitch | Creator Dashboard → Settings → Stream |
| TikTok | Live Studio → Go Live → streaming software (needs an eligible account) |
| Custom RTMP | Whatever the service gave you |

## Testing without an external account

You do not need a real platform. `docker-compose.e2e.yml` adds a disposable RTMP receiver that
stands in for one:

```bash
cd infrastructure/docker
docker compose -f docker-compose.yml -f docker-compose.e2e.yml --env-file .env up -d
```

It listens on `1935` *inside the compose network* — which is how the relay reaches it, and what the
commands below use — and exposes its own control API on `9998`. Add a **Custom RTMP** destination
with server URL `rtmp://fake-platform:1935/live` and any stream key, then go live.

From the **host** it is published on `1936`, not `1935`: the real gateway took 1935 when external
encoder ingest was enabled (ADR 0022), and two containers cannot publish the same host port. Only
manual probing from the host is affected.

Confirm the stream actually arrived:

```bash
curl -s http://localhost:9998/v3/rtmpconns/list | jq '.items[] | {path, state}'
# { "path": "live/<your key>", "state": "publish" }
```

Inspect what it received:

```bash
docker run --rm --network livestream-phase1_default \
  --entrypoint ffprobe livestream-phase1-relay:latest \
  -v error -show_entries stream=codec_name,width,height \
  -of default=noprint_wrappers=1 rtmp://fake-platform:1935/live/<your key>
# codec_name=h264  width=1280  height=720
# codec_name=aac
```

**Test the isolation guarantee** by severing the connection mid-broadcast:

```bash
ID=$(curl -s http://localhost:9998/v3/rtmpconns/list | jq -r '.items[0].id')
curl -s -X POST http://localhost:9998/v3/rtmpconns/kick/$ID
```

The destination should go `Live → Reconnecting`, the session badge should stay **Live** throughout,
and the destination should return to **Live** on its own within a few seconds.

## Running the end-to-end specs

They need a running stack plus the e2e overlay:

```bash
cd infrastructure/docker
docker compose -f docker-compose.yml -f docker-compose.e2e.yml --env-file .env up -d --build

cd ../../apps/web
E2E_MEDIA_CONTROL_API=http://localhost:9997 \
E2E_FAKE_PLATFORM_API=http://localhost:9998 \
npm run test:e2e
```

Without Node on the host, run them from a container. Two things matter:

```bash
docker run --rm --network host \
  -v "$PWD":/src -w /src/apps/web \
  -e E2E_MEDIA_CONTROL_API=http://localhost:9997 \
  -e E2E_FAKE_PLATFORM_API=http://localhost:9998 \
  mcr.microsoft.com/playwright:v1.62.1-noble \
  sh -lc "npm ci && npx playwright test"
```

- **`--network host` is required.** `getUserMedia` only works in a secure context, and
  `http://localhost` is the only plaintext origin that qualifies. Reaching the studio by a
  compose-network hostname instead leaves **Start Live** permanently disabled, because the studio
  correctly refuses to open a camera on an insecure origin.
- **The Playwright image tag must match the installed `@playwright/test` version**, or Chromium is
  missing from the image.

## Common problems

### The destination sits in CONNECTING and never goes live

The encoder started but the platform is not accepting the stream.

```bash
docker compose logs relay | grep -i "destination\|connected\|failed"
```

- Confirm the server URL and key are the pair the platform issued — a key from one and a URL from
  another fails exactly like this.
- Some platforms only accept a connection once a broadcast has been created in their dashboard.
- Confirm the relay can reach the platform at all:
  `docker compose exec relay sh -c "getent hosts live.twitch.tv"`.

After `Distribution__ConnectTimeoutSeconds` (default 45) it is treated as a failed attempt and
retried.

### The destination says Live, bytes are flowing, and the platform shows nothing

The single hardest failure in this system to diagnose, because nothing reports it. Facebook Live
accepts a stream with **no audio track**, counts it as live, and never shows it to anybody. The same
happens to an audio-only stream on every platform.

The relay generates the missing kind rather than letting this happen, so first confirm it did:

```bash
docker compose logs relay | grep -i "source carries"
# Relay <id> source carries video=Present, audio=Absent; synthesising the missing audio as silence
```

Then check what the encoder actually opened:

```bash
docker compose logs relay | grep -i "connected to"
# … Stream #0:0: Video: h264 … | Stream #1:0: Audio: pcm_u8, 48000 Hz, stereo …
```

Two streams means the platform is being sent both kinds. If you instead see:

```
Source inspection exited 1; sending the stream exactly as it arrives
```

…the relay could not establish what the source carries and deliberately changed nothing — sending
generated audio over a source that has some would replace the broadcaster's voice with silence for
the whole show, so an uncertain answer is never acted on. The usual cause is the source not
publishing yet, which resolves itself on the next attempt. If it persists, confirm ffprobe is
present and the read credential works:

```bash
docker compose exec relay which ffprobe
```

If neither line appears at all, `Relay__SynthesizeMissingTracks` is set to `false`.

### The destination goes straight to ERROR with `LIVE_016_DESTINATION_REJECTED`

The platform refused the credentials. This is deliberately **not** retried: retrying a rejected key
burns the budget and risks the account being rate-limited.

Fix the key with **Edit**, then press **Retry** on the destination. The session keeps running
throughout.

### `LIVE_018_RELAY_UNAVAILABLE`

The API cannot reach the relay service.

```bash
docker compose ps relay
docker compose exec api wget -qO- http://relay:8080/health/ready
```

Confirm `RELAY_SECRET` is identical for both services — a mismatch shows up as `401` on every relay
call, which the API reports as unavailable.

### Destinations never start when the session goes live

```bash
docker compose logs api | grep "Destinations started"
```

- A destination must be **enabled** and in `IDLE`, `STOPPED`, or `ERROR` to be started.
- `Distribution__MaxDestinationsPerSession` (default 5) caps how many can be added at all.

### The relay keeps restarting with a source error

The relay could not read the session from the gateway.

```bash
docker compose logs mediamtx | grep -i rtsp
docker compose logs api | grep "Relay read credential issued"
```

- `rtsp: yes` must be set in `mediamtx.yml` (port 8554 stays unpublished).
- `Relay__SourceRtspBaseUrl` must be `rtsp://mediamtx:8554` — the internal address, not localhost.

Source loss is recovered inside the relay rather than reported as a destination failure, because a
broadcaster reconnecting legitimately drops the read. After
`Relay__SourceRecoveryWindowSeconds` (default 150) it gives up and reports upward.

### The relay says it selected libx264, but this machine has a GPU

Two different encoders run in this system, and they fail for different reasons:

| Encoder | Where it runs | What it encodes |
|---|---|---|
| The **browser's** | The broadcaster's own machine | The WebRTC stream going to the gateway |
| The **relay's** | The relay container | The RTMP stream going to each platform |

The relay's runs in Docker and is given no GPU by default. Check what it chose:

```bash
docker compose logs relay | grep "Selected video encoder"
# Selected video encoder libx264 (CPU) (hardware=False)   <- no GPU reached the container
```

To give it one, add the NVIDIA overlay:

```bash
docker compose -f infrastructure/docker/docker-compose.yml \
               -f infrastructure/docker/docker-compose.nvidia.yml up -d
```

```bash
docker compose logs relay | grep -i encoder
# Using configured encoder NVIDIA NVENC
```

**`--gpus all` on its own is not enough**, and the way it fails is thoroughly misleading:
`nvidia-smi` works inside the container, CUDA works, and NVENC still reports `Cannot load
libnvidia-encode.so.1` followed by a complaint about the driver version — with a perfectly current
driver. The encode libraries sit behind a separate driver capability. The overlay sets
`NVIDIA_DRIVER_CAPABILITIES: video,compute,utility`, and `video` is the one that matters.

Check the capability is reaching the container:

```bash
docker compose exec relay ls /usr/lib/x86_64-linux-gnu/libnvidia-encode.so.1
```

On Windows this works through Docker Desktop with the WSL2 backend. **Quick Sync and VAAPI do not** —
WSL2 does not expose `/dev/dri` for video encode, so Intel and AMD hardware encoding needs a Linux
host.

The relay trials whatever it selects before using it, so a wrong setup falls back to libx264 and says
so at startup rather than failing somebody's broadcast.

### CPU is pinned with several destinations

Each destination runs its own encoder, and video is transcoded by default because browsers may
publish VP8 while RTMP needs H.264.

If your broadcasters negotiate H.264, skip video re-encoding entirely:

```bash
RELAY_VIDEO_CODEC=copy
```

This is much cheaper and **fails outright on a VP8 source**, which is why it is not the default.
Otherwise lower `RELAY_VIDEO_BITRATE_KBPS`, or reduce `Distribution__MaxDestinationsPerSession`.

### Account linking says the provider is not configured

Expected until provider client credentials are set. Every platform still works with a stream key.

```bash
YOUTUBE_CLIENT_ID=...      # Google Cloud project, YouTube Data API v3 enabled
YOUTUBE_CLIENT_SECRET=...
FACEBOOK_CLIENT_ID=...     # Meta app with publish_video (requires app review)
FACEBOOK_CLIENT_SECRET=...
```

The OAuth callback URL you register with the provider must match the `redirectUri` the studio sends,
exactly — including scheme, port, and trailing path.

### A stored credential cannot be read (`LIVE_023_SECRET_PROTECTION_FAILED`)

The encryption key that produced it is no longer configured. Either `SECRETS_KEY` changed, or a key
was removed while rows still referenced it.

Restore the previous key alongside the current one:

```yaml
Secrets__PrimaryKeyId: "local-2"
Secrets__Keys__local-1: <the old key>    # still needed for reads
Secrets__Keys__local-2: <the new key>
```

Re-saving each destination's stream key re-encrypts it under the new primary. Only once every row
has been re-saved is it safe to remove the old key.

## Running the checks

```bash
# Backend — includes the destination state machine, orchestration and encryption suites, and
# the relay encoder-argument tests (services/relay/LiveStream.Relay.Tests, in the same solution).
cd services/api
dotnet build LiveStream.sln          # warnings are errors
dotnet test LiveStream.sln

# Studio
cd apps/web
npm run lint && npm run typecheck && npm test && npm run build
```

## Building without a local toolchain

The whole build runs in containers if the .NET SDK or Node is not installed on the host:

```bash
docker run --rm -v "$PWD":/src -w /src/services/api \
  mcr.microsoft.com/dotnet/sdk:9.0 dotnet test LiveStream.sln

docker run --rm -v "$PWD":/src -w /src/apps/web node:22-alpine \
  sh -lc "npm ci && npm test"
```

On Windows, delete `bin/` and `obj/` first — host build artifacts confuse a Linux container build
(`NETSDK1064`).

# Phase 1 — Local setup and verification

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 9.0+ | API build and tests |
| Node.js | 22+ (24 recommended) | Studio build and tests |
| Docker + Compose | recent | PostgreSQL, MediaMTX, database tests |

Docker is optional for the default test run: the unit and integration suites use in-memory SQLite.
It is required for the PostgreSQL migration tests, which otherwise report as **skipped**.

## Running the stack

```bash
cd infrastructure/docker
cp .env.example .env
```

Fill in `.env` — both values are required and the stack refuses to start without them:

```bash
# 48 random bytes, base64
openssl rand -base64 48      # -> JWT_SIGNING_KEY

# 32 random bytes, hex
openssl rand -hex 32         # -> INTERNAL_API_SECRET
```

Then:

```bash
docker compose --env-file .env up --build
```

| Service | URL | Notes |
|---|---|---|
| Studio | http://localhost:3000 | |
| API | http://localhost:8080 | OpenAPI at `/openapi/v1.json` in Development |
| HLS playback | http://localhost:8888 | |
| WHIP / WHEP | http://localhost:8889 | |
| Gateway control API | *not published* | Reachable only inside the compose network |

## Running without Docker

```bash
# API — needs a PostgreSQL instance reachable at the configured connection string
cd services/api
dotnet run --project src/LiveStream.Api

# Studio
cd apps/web
cp .env.example .env.local
npm install
npm run dev
```

Set at minimum:

```bash
export Jwt__SigningKey="$(openssl rand -base64 48)"
export InternalApi__SharedSecret="$(openssl rand -hex 32)"
export ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=livestream;Username=livestream;Password=livestream"
```

## Verifying the broadcast path

### Automated — the fastest check

With the stack running, this publishes real WebRTC media through it and asserts the whole journey:

```bash
cd apps/web
npx playwright install chromium   # first time only
npm run test:e2e
```

It signs in, creates a session, captures from a synthetic camera, goes live, confirms the server
agrees, plays the stream back in a second browser, stops, and checks the recording is finalized. A
second spec severs the publisher at the gateway and asserts the session recovers rather than ending.

Add `--headed` (`npm run test:e2e:headed`) to watch it happen.

### Manual — with your own camera

Worth doing once, because the automated run uses a synthetic device.

1. Register at http://localhost:3000/register.
2. Create a session; you land in the studio.
3. Press **Enable camera and microphone** and allow the browser prompt.
4. Confirm the preview shows your camera and both device pickers list real device names.
5. Press **Start Live**. Within a few seconds the badge should read **Live**.
6. Confirm on the gateway that media is arriving:
   ```bash
   docker compose exec mediamtx wget -qO- http://localhost:9997/v3/paths/list
   ```
   The session's path should show `"ready": true` and a rising `bytesReceived`.
7. Open http://localhost:3000/watch/{sessionId} in another browser. The stream should play.
   Expect several seconds of HLS latency — that is inherent to the format, not a fault.
8. Check the health row: duration ticking, viewers ≥ 1, health **Good**, connection **Stable**.
9. **Reconnect test.** Disable Wi-Fi for ~15 seconds. The studio should show **Reconnecting** with a
   countdown, and return to **Live** when the network comes back. The session must not end.
10. Press **Stop Live**. The status should reach **Ended**.
11. Confirm the recording was captured:
    ```bash
    docker compose exec mediamtx ls -R /recordings
    curl -H "Authorization: Bearer $TOKEN" \
      http://localhost:8080/api/v1/live-sessions/$SESSION_ID/recordings
    ```
    Status should be `READY` with a non-zero `sizeBytes`.

## Common problems

### "Your browser will not share the camera on an insecure connection."

`getUserMedia` requires a secure context. `http://localhost` counts as secure; `http://192.168.x.x`
does not. Use localhost, or put an HTTPS proxy in front.

### Start Live fails with `LIVE_012_MEDIA_GATEWAY_UNAVAILABLE`

The API cannot reach the gateway control API.

```bash
docker compose ps
docker compose logs mediamtx
docker compose exec api wget -qO- http://mediamtx:9997/v3/config/global/get
```

Check that `Media__MediaMtx__ControlApiUrl` points at `http://mediamtx:9997` (the internal address),
not `localhost`.

### The session reaches STARTING but never LIVE, then FAILED with `LIVE_003`

Media is not arriving. The commonest cause is WebRTC UDP being blocked.

- Confirm `8189/udp` is published and not firewalled.
- Check the browser console for ICE failures.
- Behind restrictive NAT, STUN is not enough and TURN is required — see "Users on restrictive
  networks cannot connect" below.

### The gateway rejects the publish with 401

The auth callback is failing.

```bash
docker compose logs api | grep -iE "media publish denied|carried no credential"
```

The log states which check failed: unknown credential, expired, path mismatch, wrong scope, or a
session not accepting ingest.

If it says **"carried no credential"**, the credential is not surviving the gateway's transport. That
line also reports which fields the callback *did* carry (names only, never values) — compare it with
what the broadcaster sends. This is a real failure mode: MediaMTX only populates the callback's
`token` field when it validates JWTs itself, so the studio sends HTTP Basic with the token as the
password instead.

Also confirm `INTERNAL_API_SECRET` is identical for both services. The gateway sends it as the `?s=`
query parameter on its callback URL, injected via `MTX_AUTHHTTPADDRESS` — MediaMTX does **not** expand
`${VAR}` placeholders inside `mediamtx.yml`, so setting it there would send the literal text.

Check the effective value:

```bash
docker run --rm --network livestream-phase1_default curlimages/curl \
  -fsS http://mediamtx:9997/v3/config/global/get | tr ',' '\n' | grep authHTTP
```

### Recording is FAILED with "No media was captured"

The API and gateway are not sharing the recordings volume, or nothing was recorded.

```bash
docker compose exec mediamtx ls -la /recordings
docker compose exec api ls -la /recordings
```

Both must show the same contents. `recordPath` in the gateway config must place files under
`/recordings/{mediaPathName}/`, which is what `FileSystemRecordingStore` reads.

### Realtime updates show "Polling" instead of "On"

The SignalR connection is not established. The studio stays correct — REST polling continues — but
updates are slower.

- Confirm the studio's origin is in `Cors__AllowedOrigins__0`.
- Check the browser network tab for the `/hubs/live` negotiate request.
- A proxy in front of the API must allow WebSocket upgrades.

### Database tests report as skipped

Expected when Docker is unavailable. They are skipped, never silently passed. Start Docker and re-run:

```bash
cd services/api
dotnet test tests/LiveStream.DatabaseTests/LiveStream.DatabaseTests.csproj
```

## Password reset, and sending email

Email is **off by default**, and the platform ships without a mail server. With it off, nothing
silently fails: the API writes each message to its own log in full, so a reset can be completed
locally without configuring anything.

```bash
# Ask for a reset in the browser at /forgot-password, then:
docker compose logs api | grep -A 8 "Email is not configured"
# ... the reset link is in the body.
```

### Turning it on

```bash
SMTP_ENABLED=true
SMTP_HOST=smtp.example.com
SMTP_PORT=587              # submission port; STARTTLS is on by default
SMTP_USERNAME=apikey
SMTP_PASSWORD=...          # a secret — from the environment, never committed
SMTP_FROM_ADDRESS=no-reply@yourdomain.com
SMTP_FROM_NAME="Your Studio"
```

Links in the email point at `WEB_ORIGIN`, the same address join links use. There is no second place
to configure it.

**A production deployment needs this set.** Without it a forgotten password is unrecoverable by
anyone who cannot read the server's logs.

### Testing against a real mailbox

The end-to-end overlay starts a disposable mail server that accepts everything and shows you what
arrived — the same idea as the fake RTMP platform.

```bash
docker compose -f infrastructure/docker/docker-compose.yml \
               -f infrastructure/docker/docker-compose.e2e.yml up -d
# Every message the platform sends: http://localhost:8025
```

### Common problems

**The email never arrives, and the log says `Sending email to … failed`.** The mail server refused
the connection or the credentials. The reset token is still valid — nothing was consumed — so the
person can try again once it is fixed.

**The link goes to a page that does not exist.** `WEB_ORIGIN` points somewhere other than the
studio. The link is built server-side from that value.

**"This reset link is no longer valid."** Expected for a link that is over an hour old, has already
been used, or was superseded by a newer request. One message for all three, deliberately — see
[ADR 0019](../decisions/0019-password-reset-and-email.md).

**Somebody was signed out on their phone after resetting.** Intended. A reset revokes every session,
because the reason for resetting is often that somebody else is signed in.

## Running the checks

```bash
# API
cd services/api
dotnet build LiveStream.sln          # warnings are errors
dotnet test LiveStream.sln

# Studio
cd apps/web
npm run lint
npm run typecheck
npm test
npm run build
```

## Migrations

```bash
cd services/api

# Apply to a live database
dotnet-ef database update --project src/LiveStream.Infrastructure --startup-project src/LiveStream.Infrastructure

# Regenerate the committed SQL script after a model change
dotnet-ef migrations script --idempotent \
  --project src/LiveStream.Infrastructure --startup-project src/LiveStream.Infrastructure \
  --output ../../infrastructure/db/migrations.sql

# Confirm the model and migrations agree
dotnet-ef migrations has-pending-model-changes \
  --project src/LiveStream.Infrastructure --startup-project src/LiveStream.Infrastructure
```

`Database:MigrateOnStartup` applies migrations when the API starts. It is on for local and container
use. Turn it **off** for multi-instance production and run migrations as a deploy step, so instances
cannot race each other.

## Users on restrictive networks cannot connect

Symptom: the camera preview works, **Start Live** is pressed, the session reaches `STARTING` but
never `LIVE`, and fails after the start timeout. It works on your network and not on theirs.

This is almost always NAT traversal. A STUN server — the default — only tells a peer its public
address; it cannot relay media. Symmetric NAT, and many corporate and mobile carrier networks,
need a **TURN** relay.

TURN is a configuration change, not a code change. ICE servers are served by the API with each
ingest credential, so nothing needs rebuilding:

```jsonc
// appsettings.json, or Media__Ice__Servers__… environment variables
"Media": {
  "Ice": {
    "Servers": [
      { "Urls": [ "stun:stun.example.com:3478" ] },
      {
        "Urls": [ "turn:turn.example.com:3478?transport=udp" ],
        "Username": "…",
        "Credential": "…"
      }
    ]
  }
}
```

Supply the credential from a secret store, never from committed configuration. Because the studio
requests a fresh credential for every connection attempt, ephemeral time-limited TURN credentials
work without further changes.

Confirm what a broadcaster is actually being handed:

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:8080/api/v1/live-sessions/$SESSION_ID/sources/credentials | jq .iceServers
```

See `docs/decisions/0008-nat-traversal-and-studio-resume.md`.

## Reloading the studio during a broadcast

This is supported. The page reload destroys the peer connection, the server notices ingest has
stopped and holds the session in `RECONNECTING`, and the reloaded studio republishes into the same
session automatically. Session id, start time, duration, and recording all continue.

Expect a few seconds of interruption for viewers. If it does not recover, check the browser console
for a camera permission error — the studio cannot resume without media.

Opening the studio in a **second tab** while the first is still publishing is safe: the second tab
sees that ingest is already connected and watches rather than competing for the ingest path.

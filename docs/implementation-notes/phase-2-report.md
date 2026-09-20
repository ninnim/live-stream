# Phase 2 — Multi-Platform Distribution: implementation report

## What was built

One Live Session now republishes to external platforms without the creator changing anything about
how they broadcast. They press **Start Live** as before; every enabled destination is started for
them, monitored independently, retried on its own schedule, and stopped when the session ends.

```text
                    ┌─────────────────────────────────┐
  Browser ──WHIP──▶ │  MediaMTX  │  path per session  │ ──LL-HLS/WHEP──▶ platform viewers
                    └──────┬──────────────────────────┘
                           │ RTSP (internal only, READ-scoped credential)
                           ▼
                    ┌─────────────┐
                    │    relay    │  one FFmpeg per destination
                    └──┬───┬───┬──┘
                       │   │   │  RTMP/RTMPS
              YouTube ◀┘   │   └▶ Twitch / TikTok / custom RTMP
                      Facebook
```

The control plane never touches media. It decides what should be running and reconciles that against
what the relay reports, the same way it already reconciles session state against the media gateway.

## Acceptance criteria

From `implementation/phase-2-multi-platform-distribution.md`:

| Criterion | Status | Where it is proven |
|---|---|---|
| Core stream can remain LIVE if one destination fails | **Verified with real media, through the UI** | `distribution.spec.ts` outage test; live run below; `DestinationTests.A_failing_destination_does_not_affect_the_session_or_its_siblings` |
| Destination status is visible independently | **Verified** | Per-destination status, uptime, throughput and audit trail; `Destination_events_record_the_lifecycle` |
| Credentials never appear in frontend responses | **Verified** | `The_stream_key_never_appears_in_any_response` asserts against raw JSON, not the typed contract |
| Transient publishing failures retry | **Verified with real media** | Outage test; `A_transient_failure_schedules_a_retry_with_backoff`, `Retries_are_bounded_and_end_in_error` |
| Provider-specific errors normalized to internal codes | **Verified** | `ProviderHttp.Classify`; `LIVE_016`–`LIVE_023` |

Scope from the same document — destination model, provider adapter interface, YouTube, Facebook,
TikTok, custom RTMP, destination status and retries, credential storage and rotation, destination
management UI — is all present. Twitch was added as well, since it is stream-key only and cost
nothing beyond a descriptor.

## The end-to-end run

Executed against the real stack: PostgreSQL, MediaMTX, the API, the relay, and a throwaway MediaMTX
container standing in for an external platform. Real H.264/AAC media, real encoder processes, real
RTMP.

**1. The full path works.**

```
fake-platform  │ [RTMP] [conn 172.18.0.5:42134] is publishing to path 'live/e2e-test-key',
               │        2 tracks (H264, MPEG-4 Audio)
relay          │ Relay started for c6ae696f… provider=CustomRtmp session=655aa785…
               │ Relay c6ae696f… connected to CustomRtmp
ffprobe        │ codec_name=h264  width=1280  height=720
               │ codec_name=aac   sample_rate=44100
```

Session `LIVE` at t+0, destination `Connecting` at t+3s, `Live` with bytes flowing at t+6s.

**2. A destination failing does not touch the session.** The platform was killed mid-broadcast:

```
t+4s   SESSION=LIVE  destination=Live       attempts=0
t+8s   SESSION=LIVE  destination=Retrying   attempts=1  LIVE_017_DESTINATION_UNAVAILABLE
t+16s  SESSION=LIVE  destination=Connecting attempts=1
t+28s  SESSION=LIVE  destination=Retrying   attempts=2  LIVE_017_DESTINATION_UNAVAILABLE
```

**3. It recovers by itself.** The platform was restored:

```
t+28s  SESSION=LIVE  destination=Connecting attempts=3
t+32s  SESSION=LIVE  destination=Live       attempts=3
fake-platform │ is publishing to path 'live/e2e-test-key', 2 tracks (H264, MPEG-4 Audio)
```

**4. The audit trail reads correctly**, and the platform connection closed cleanly on stop:

```
Created · Preparing · TargetResolved · Connecting · Live
  · Retrying(LIVE_017) · Preparing · TargetResolved · Connecting
  · Retrying(LIVE_017) · Preparing · TargetResolved · Connecting
  · Live · Stopping · Stopped
```

## The bug the end-to-end run found

Byte throughput froze after every reconnect.

Each retry starts a fresh encoder whose counter begins at zero. The domain kept a high-water mark to
stop the figure going backwards, which meant that after an outage the number sat still — for minutes
on a long one — while the new attempt caught up to the old total.

Each attempt now banks the previous total as a baseline and adds to it. Verified on the real stack:
`5,638,547 → 5,924,938` bytes across a reconnect, still climbing.

Worth noting how it hid: the unit test asserting *"the counter never goes backwards"* passed
perfectly, both before and after. Nothing short of watching a real reconnect would have shown it.

## Verified vs. not verified

Stated plainly, because the difference matters.

**Verified by execution:**

- Stream-key destinations, end to end, with real media reaching a real RTMP endpoint.
- Failure isolation, retry with backoff, and unattended recovery, against a genuinely dead platform.
- Encryption at rest, including that the ciphertext round-trips and the plaintext is absent from the
  database column and from every API response.
- Authorization and tenant isolation on every destination route.
- The whole journey **through the studio UI**, in a real browser: adding a destination, going live,
  watching it connect, severing the platform, watching it recover.
- 335 backend tests (235 unit, 100 integration), 75 frontend tests, and 10 end-to-end tests
  (7 from Phase 1, 3 new) — all passing against the running stack.

**Written but not executed against the live provider:**

- The YouTube OAuth flow and its `liveBroadcasts` / `liveStreams` / `bind` calls.
- The Facebook OAuth flow and its `live_videos` calls.

These are implemented against the documented APIs, but running them needs a Google Cloud project
with the YouTube Data API enabled and a Meta app with `publish_video` approval — credentials that
belong to an organisation, not a build environment. Their unit-testable parts (URL construction,
error classification, the Facebook stream-URL split) are covered. **Do not treat linked accounts as
proven until you have run one against your own provider app.**

Until credentials are configured, `GET /api/v1/distribution/providers` reports
`linkedAccountConfigured: false`, and starting a consent flow returns
`LIVE_019_PROVIDER_ACCOUNT_UNAVAILABLE` with an explanation, rather than sending the operator to a
screen that cannot work.

## What changed outside Phase 2

- **`rtsp: yes`** in the gateway config, port unpublished, so the relay can read. RTMP and SRT stay
  off.
- **Read authorization** now accepts a READ-scoped credential before falling back to visibility.
  This closed a real gap: a **private** session could not previously have been republished at all.
- **`DESTINATION_MANAGE`** added to the permission table; granted to Owner, Admin and Producer, and
  deliberately withheld from Host.
- **`migrations-phase1.sql` renamed to `migrations.sql`** — it is idempotent and now covers both
  phases. Doc references updated.

## Configuration

New required values (see `infrastructure/docker/.env.example`):

| Variable | Purpose |
|---|---|
| `RELAY_SECRET` | Shared secret between API and relay. Separate from the gateway secret on purpose. |
| `SECRETS_KEY` | Base64, exactly 32 bytes. Encrypts stream keys and OAuth tokens. |

**Losing `SECRETS_KEY` makes every stored destination credential permanently unreadable.** Back it
up and use a real secret store in production. To rotate: add a second `Secrets__Keys__<new-id>`,
repoint `Secrets__PrimaryKeyId`, and keep the old key configured until every row has been re-saved.

Optional: `YOUTUBE_CLIENT_ID` / `YOUTUBE_CLIENT_SECRET`, `FACEBOOK_CLIENT_ID` /
`FACEBOOK_CLIENT_SECRET`, and the `RELAY_VIDEO_*` encoder settings.

## Known limitations

- **Each destination runs its own encoder.** Five destinations at 720p is a meaningful CPU load on
  one host. `Relay__VideoCodec=copy` removes video re-encoding entirely, but only works when the
  browser negotiated H.264. A shared transcode fanned out to all destinations would be the real fix
  and is not built.
- **The relay is single-instance.** It holds its process table in memory; two replicas would each
  supervise their own relays with no coordination. Scaling out needs the same shared-state work as
  the rest of the platform ([ADR 0004](../decisions/0004-redis-deferred.md)).
- **TikTok is stream-key only.** Its API requires approval and account eligibility that most
  operators will not have; a pasted key from TikTok Live Studio works today for an eligible account.
- **Twitch is stream-key only.** Its API adds nothing to the publish path that the key does not
  already do.
- **No per-destination bitrate control.** Every destination gets the same encoder settings. Per
  platform ladders are a Phase 4 concern.

## Where to look

| Concern | File |
|---|---|
| Destination lifecycle rules | `services/api/src/LiveStream.Domain/Distribution/DestinationStateMachine.cs` |
| Destination aggregate | `.../Distribution/StreamDestination.cs` |
| Start, stop, retry, reconcile | `services/api/src/LiveStream.Application/Distribution/DestinationOrchestrator.cs` |
| The isolation seam | `.../Application/Abstractions/IDistributionCoordinator.cs` |
| Encryption at rest | `services/api/src/LiveStream.Infrastructure/Security/AesGcmSecretProtector.cs` |
| Provider adapters | `.../Infrastructure/Distribution/Adapters/` |
| Encoder supervision | `services/relay/LiveStream.Relay/RelayWorker.cs` |
| Studio UI | `apps/web/src/components/studio/DestinationPanel.tsx` |

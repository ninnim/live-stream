# ADR 0009 — Multi-platform distribution

**Status:** Accepted
**Date:** 2026-09-04
**Phase:** 2 — Multi-Platform Distribution

How one Live Session reaches YouTube, Facebook, TikTok, Twitch, and arbitrary RTMP endpoints
without the creator changing anything about how they broadcast.

## 1. Egress runs in its own service

### Context

MediaMTX ingests and serves media, but it does not republish to external RTMP endpoints. Something
has to pull the session and push it onward, and that something is an encoder process.

Three options:

| Option | Verdict |
|---|---|
| `runOnReady` hooks in the gateway config | Static per path. Destinations are added and removed per session, so this cannot express them. |
| Spawn encoders inside the API process | Ties control-plane availability to how many destinations happen to be live, and puts media tooling in the image that holds the user database. |
| A separate relay service | Chosen. |

### Decision

A dedicated `relay` service supervises one FFmpeg process per destination and exposes a private HTTP
API (`POST /relays`, `DELETE /relays/{id}`, `GET /relays`). The API talks to it through
`IStreamRelay`, exactly as it talks to the media gateway through `IMediaGateway`.

The relay publishes no ports. A request to it carries a stream key in the body, so it is reachable
only from inside the compose network — the same posture as the gateway's control API.

### Consequences

- The API image contains no media tooling; the relay image is the only one with FFmpeg.
- Relays can be scaled independently of the control plane.
- The relay is stateless across restarts. It reports what it is running; the API reconciles against
  that and restarts anything missing, so a relay restart costs a reconnect rather than a lost
  destination.

## 2. Read access for the relay

The relay pulls over RTSP from the gateway, which meant enabling `rtsp: yes` — a protocol Phase 1
deliberately turned off.

Port 8554 is **not** published in `docker-compose.yml`. Enabling the protocol does not weaken
authorization: every read still round-trips to the control plane's auth hook. The relay presents a
short-lived **READ-scoped** ingest credential as its RTSP password, mirroring how the studio presents
its publish credential over WHIP.

This also fixed a real gap. Read authorization previously depended only on session visibility, so a
**private** session could not have been republished at all — the relay would have been denied. Read
now accepts a valid READ-scoped credential first and falls back to visibility, which keeps public
links playable without a token while making private sessions distributable.

RTSP rather than HLS because FFmpeg reads it far more reliably and without a segmented format's
added latency.

## 3. Video is transcoded by default

Browsers publish VP8 or H.264 over WebRTC depending on what was negotiated; RTMP carries H.264 and
AAC. Audio therefore always needs transcoding (Opus → AAC), and video needs it whenever the browser
chose VP8.

`Relay__VideoCodec` defaults to `libx264`, which is always correct. Setting it to `copy` skips video
re-encoding entirely — much cheaper — but fails outright on a VP8 source. It is offered as an
explicit opt-in for deployments that know their browsers negotiate H.264, not as a default that
works most of the time and breaks confusingly the rest.

The cost is real: each destination runs its own encoder. `Distribution:MaxDestinationsPerSession`
(default 5) bounds it.

## 4. Failure isolation is structural, not conventional

The Phase 2 acceptance criterion is *"Core stream can remain LIVE if one destination fails."*

Rather than relying on every future caller remembering that, the seam enforces it:

- `IDistributionCoordinator` has exactly two methods, both returning `Task` rather than a result, so
  session logic has nothing to branch on even if someone later wanted to.
- Both implementations swallow and record errors instead of throwing.
- Destinations have their own state machine, their own reconciler pass, their own audit trail, and
  their own background monitor — a distribution failure cannot stop session health reconciliation,
  which is what keeps reconnect working.

Verified against real infrastructure: with a session live and publishing, the destination platform
was killed. The session stayed `LIVE` for the whole outage while the destination went
`Live → Retrying → Connecting → Retrying` and, once the platform returned, recovered to `Live` on
its own.

## 5. Retry classification

Provider errors are normalized to internal codes before anything branches on them, and the only
distinction that matters operationally is **retryable or not**:

| Situation | Code | Retried |
|---|---|---|
| Platform refused the key | `LIVE_016_DESTINATION_REJECTED` | No |
| Platform unreachable or dropped | `LIVE_017_DESTINATION_UNAVAILABLE` | Yes |
| Relay itself unavailable | `LIVE_018_RELAY_UNAVAILABLE` | Yes |
| Linked account needs consent | `LIVE_019_PROVIDER_ACCOUNT_UNAVAILABLE` | No |
| Provider API error | `LIVE_020_PROVIDER_API_ERROR` | Depends on status |
| Quota or eligibility | `LIVE_021_PROVIDER_QUOTA_EXCEEDED` | No |

Retrying a rejected credential burns the budget and risks the account being rate-limited; wrongly
giving up on a transient failure ends distribution for the rest of the broadcast. Unrecognised
errors are therefore treated as **retryable**, with exponential backoff and a bounded attempt count.

Source loss is handled separately, inside the relay. A broadcaster reconnecting drops the RTSP read,
and reporting that as a destination failure would spend the destination's retry budget on something
the destination did not do. The relay recovers from source loss internally within
`Relay__SourceRecoveryWindowSeconds` and reports only destination failures upward.

## 6. Credentials at rest

Stream keys and OAuth tokens are the only long-lived third-party secrets the platform stores.

- **AES-256-GCM**, not CBC: GCM authenticates as well as encrypts, so a tampered row fails to
  decrypt rather than yielding attacker-influenced plaintext. That matters because the plaintext
  becomes an RTMP target on an encoder command line — a malleable ciphertext would be a redirection
  primitive.
- **Random nonce per encryption**, so identical keys do not produce identical ciphertext.
- **Key id travels in the ciphertext** (`v1:{keyId}:{base64}`), so keys rotate by adding a new
  primary and keeping the old one configured for reads.
- **`DESTINATION_MANAGE`** is a separate permission from `LIVE_SESSION_START`. Running a broadcast
  does not imply custody of the workspace's platform credentials, so `Host` can go live but not
  repoint the channel.

No endpoint returns a stream key, and no response contract has a field one could travel in. Ingest
URLs are returned with any query string stripped, because some platforms embed the key there.

## 7. Stream keys and linked accounts

Two credential modes:

- **Stream key** — the operator pastes the endpoint and key from the platform's own dashboard.
  Works today for every supported platform with no app registration, no API access, and no review.
- **Linked account** — OAuth. The platform API creates a broadcast per session, so each session
  appears as its own YouTube broadcast or Facebook live video with the right title and privacy, and
  no key is ever pasted.

Both are implemented. Linked accounts are inert until provider client credentials are configured;
until then the API reports `linkedAccountConfigured: false` and refuses to start a consent flow that
cannot work, rather than sending the operator to a broken screen.

**What is not verified here:** the OAuth flows and broadcast-creation calls are written against the
documented provider APIs but have not been executed against live YouTube or Facebook, because that
requires a Google Cloud project and a Meta app with `publish_video` approval — neither of which can
be obtained from a build environment. Stream-key destinations were verified end to end with real
media. See the Phase 2 report for exactly what was and was not exercised.

### YouTube specifics

Broadcasts are created with `enableAutoStart` and `enableAutoStop`. YouTube then transitions the
broadcast itself when media arrives, which is markedly more reliable than racing it with an explicit
`transition` call — that approach fails whenever the call lands a moment before YouTube has accepted
the first bytes. An explicit transition to `complete` still runs on stop, to close out a broadcast
whose media stopped without YouTube noticing.

### Facebook specifics

Facebook returns a single `secure_stream_url` with the key already appended. It is split into
endpoint and key on arrival so the key can be kept out of logs on both sides, and rejoined only
inside the encoder command line. Facebook issues no refresh token; the long-lived access token is
re-exchanged to extend it, so it is stored in both token fields.

## 8. OAuth state

State is stored server-side, single-use, and consumed by the callback. The workspace and user come
from the stored entry, never from the request — without that binding, a crafted callback URL could
silently link an attacker's channel to a victim's workspace, and every subsequent broadcast would
republish to it.

Backed by `IDistributedCache`: memory in a single instance, Redis when one is configured, with no
code change. Losing it on restart only means the operator restarts the linking flow, which is one of
the few places an in-memory default is genuinely acceptable
(see [ADR 0004](0004-redis-deferred.md)).

## 9. What the byte counter taught

The first implementation kept a high-water mark of bytes sent, to stop the figure going backwards
when a relay restarted with a zeroed counter.

Running the real outage test showed what that actually looks like: after reconnecting, throughput
sat frozen at the pre-outage total for minutes while the new encoder caught up. Not wrong, exactly —
just uninformative at the one moment an operator is watching it most closely.

Each attempt now banks the previous total as a baseline and adds to it, so the figure keeps climbing
across a reconnect. It is a small thing, and unit tests would never have found it: the assertion
"does not go backwards" passed perfectly.

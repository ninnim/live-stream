# Live Streaming Platform — Full Product & Technical Blueprint

**Document Version:** 1.1  
**Date:** 2026-08-30  
**Status:** Architecture & Implementation Blueprint  
**Primary Goal:** Build a modern live-streaming platform that supports multiple devices, multiple destinations/platforms, reliable streaming under unstable networks, professional production workflows, analytics, and future AI-powered live operations.

---

## 1. Product Vision

Build a **Live Streaming Operating Platform** rather than a simple video player. The platform itself is the broadcaster: a creator should be able to open the web/mobile app, choose camera and microphone, configure the live session, and broadcast directly without installing or operating OBS.

The platform should allow a creator, business, organization, or media team to:

- Start one live session.
- Stream directly from phone or browser as the primary workflow.
- Optionally accept desktop apps, OBS, or external encoders as advanced input sources.
- Connect multiple devices to the same live session.
- Combine multiple video/audio sources.
- Broadcast to the platform's own viewers and external destinations such as YouTube, Facebook, TikTok, Twitch, and custom RTMP/SRT endpoints where supported.
- Continue streaming through temporary network problems using reconnection and adaptive bitrate strategies.
- Record the entire session automatically when enabled.
- Monitor health, viewers, bitrate, dropped frames, latency, and destination status in real time.
- Moderate and manage chat from one place.
- Eventually use AI as a live producer, moderator, clipping engine, translator, analyst, and assistant.

### Product Principle

```text
ONE LIVE SESSION
       |
       +--------------------+
       |                    |
   INPUT SOURCES       CONTROL CENTER
       |                    |
       +---------+----------+
                 |
          MEDIA PLATFORM
                 |
      +----------+-----------+
      |          |           |
   OWN APP    EXTERNAL    RECORDING
              PLATFORMS
      |
      +------------------------------+
      |                              |
   VIEWERS                         AI LAYER
```

---

# 2. Core Product Objectives

## 2.1 Functional Objectives

1. **Native broadcasting from the platform** without requiring OBS or another broadcaster.
2. Multi-device live streaming.
3. Multi-camera streaming.
4. Multi-platform distribution.
5. Browser-based Live Studio.
6. Native Android/iOS broadcasting.
7. Optional desktop/professional encoder input.
8. Recording and replay.
9. Real-time analytics.
10. Real-time chat and moderation.
11. Stable streaming under changing network conditions.
12. Multi-user team collaboration.
13. Native broadcaster is the default workflow.
14. Optional external encoder compatibility.
15. Future AI-assisted production.

## 2.2 Non-Functional Objectives

The platform should prioritize:

- Reliability.
- Low latency where appropriate.
- Graceful degradation.
- Horizontal scalability.
- Security.
- Observability.
- Cost control.
- Simple user experience.
- Vendor-neutral media architecture where practical.
- Ability to add new streaming destinations without redesigning the core system.

---

# 3. Target Users

## 3.1 Individual Creator

Needs:

- Phone streaming.
- Quick start.
- Chat.
- Basic overlays.
- Recording.
- Social distribution.
- AI clips.

## 3.2 Business / E-commerce

Needs:

- Product live selling.
- Multiple presenters.
- Product overlays.
- Product catalog integration.
- Moderators.
- Orders/events linked to live session.
- Cross-platform broadcasting.

## 3.3 Media / Professional Team

Needs:

- Multiple sources.
- Multiple operators.
- Multiple scenes.
- Remote guests.
- Professional encoder support.
- Multi-destination monitoring.
- Recording and archive.

## 3.4 Enterprise / Organization

Needs:

- Teams.
- Roles and permissions.
- Private events.
- SSO in later stages.
- Audit logs.
- Retention controls.
- Regional/global deployment.

---

# 4. Supported Devices

## 4.1 Mobile

### Android

- Camera capture.
- Microphone capture.
- Front/rear camera switch.
- Camera resolution selection.
- Network health.
- Background limitations handled explicitly.
- Reconnect behavior.

### iOS

- Camera capture.
- Microphone capture.
- Front/rear camera switch.
- Network monitoring.
- Reconnect behavior.
- Background/lock-state limitations handled according to platform rules.

## 4.2 Web

Next.js web application should provide the **primary desktop broadcasting experience** through a Live Studio.

Core capabilities:

- Creator dashboard.
- Live control room.
- Browser camera and microphone capture where supported.
- Screen sharing.
- Multiple browser/device sources.
- Scenes and overlays.
- Chat/moderation.
- Analytics.
- Destination management.
- Recording management.
- Network health and reconnect status.

> **Product requirement:** A creator must be able to start a normal live stream from the web app without OBS.

## 4.3 Desktop / Professional Input

Desktop encoders are **optional**, not required. Support can include:

- OBS Studio via RTMP/SRT.
- Custom RTMP encoder.
- Custom SRT encoder.
- Future native desktop broadcaster.

The backend treats these as external input sources that connect to an existing Live Session.

## 4.4 Professional Sources

Future support:

- Capture cards.
- HDMI/SDI sources through external encoder.
- Professional cameras.
- NDI sources where architecture and licensing permit.
- Remote contribution feeds.

---

# 5. Multi-Device Session Model

A key product concept is the **Live Session**.

A session owns all devices, sources, destinations, permissions, analytics, chat, recording, and AI jobs.

Example:

```text
LIVE SESSION #LS-000123

Host Device
  -> Phone A / Camera 1

Secondary Device
  -> Phone B / Camera 2

Desktop
  -> Screen Share
  -> USB Microphone

Moderator
  -> Chat-only device

Control Operator
  -> Web Control Room
```

Every device gets a temporary, scoped session credential.

### Device Roles

- `HOST`
- `CAMERA`
- `GUEST`
- `SCREEN_SOURCE`
- `AUDIO_SOURCE`
- `MODERATOR`
- `CONTROL_OPERATOR`
- `VIEWER`

---

# 6. Native Broadcasting Model

The platform has two categories of input:

### 6.1 Native Inputs — First-Class

These are built directly into the product:

- Android camera + microphone.
- iOS camera + microphone.
- Browser camera + microphone.
- Browser screen sharing.
- Multiple paired mobile cameras.
- Remote guest browser sessions.

Native inputs should require no OBS installation.

### 6.2 External Inputs — Optional

These are supported for advanced users:

- OBS.
- RTMP/RTMPS encoders.
- SRT encoders.
- Professional camera encoders.

### 6.3 Target Creator Experience

```text
OPEN YOUR PLATFORM
        |
   Create Live
        |
 Camera + Mic
        |
  Choose Destinations
        |
   Start Broadcast
        |
+-----------------------+
| Your Platform Viewer  |
| YouTube                |
| Facebook               |
| TikTok                 |
| Custom Destinations    |
+-----------------------+
```

OBS is therefore a **compatibility option**, not a prerequisite.

### 6.4 Broadcasting Modes

| Mode | Primary Audience | Required External Software |
|---|---|---|
| Mobile Live | Creators | None |
| Browser Live Studio | Creators / Business | None |
| Multi-Device Live | Teams / Commerce | None |
| Pro Encoder | Professional | Optional OBS/encoder |

---

# 6. Multi-Platform Distribution

The platform should use a generic **Destination** abstraction.

```text
Destination
- id
- provider
- displayName
- endpointType
- endpointUrl
- credentialReference
- streamKeyReference
- enabled
- status
- health
```

Supported destination types should include:

- Native platform integration.
- RTMP.
- RTMPS.
- SRT where a destination supports it.
- Future destination protocols.

Potential platforms:

- YouTube Live.
- Facebook Live.
- TikTok Live where the account/API/region/product requirements permit.
- Twitch.
- Custom RTMP.
- Custom SRT.
- The platform's own CDN/player.

**Important:** External platforms have different API, account, approval, region, ingest, and policy requirements. Build a provider adapter layer so changes in one platform do not affect the core streaming engine.

---

# 6A. Core User Flow

## Creator Starts a Live Without OBS

```text
Login
  -> Create Live
  -> Choose Camera / Mic / Screen
  -> Add optional secondary devices
  -> Select destinations
  -> Configure title / privacy / thumbnail
  -> Start Broadcast
  -> Monitor health + chat
  -> End Live
  -> Recording + analytics + AI processing
```

## Multi-device Flow

```text
Control Device
      |
 Create Session
      |
 Generate Pairing Code / QR
      |
 +----+---------+
 |              |
Phone Camera A  Phone Camera B
 |              |
 +-------> Live Media Session
                    |
               Composition
                    |
              Distribution
```

The control device can be a browser, while phones act as remote cameras.

---

# 7. High-Level Architecture

```text
                           CLIENTS
  +-----------------------------------------------------------+
  | Mobile | Web Live Studio | Optional OBS/Encoder | Professional Encoder |
  +-----------------------------+-----------------------------+
                                |
                                v
                        API / Edge Gateway
                                |
              +-----------------+-----------------+
              |                                   |
              v                                   v
       Control Plane                        Media Plane
              |                                   |
      +-------+-------+                   +-------+---------+
      |               |                   |                 |
   Identity       Session              Ingest           Routing
   Teams          Control             Gateway           / Fanout
      |               |                   |                 |
      +-------+-------+                   v                 |
              |                     Transcoding             |
              |                           |                 |
              |                           v                 |
              |                     Packaging              |
              |                      / Delivery             |
              |                           |                 |
              |                    +------+-------+         |
              |                    |              |         |
              |                   CDN         External      |
              |                    |          Platforms      |
              |                    v              |          |
              |                 Viewers           +----------+
              |
              +-------------------+
                                  |
                                  v
                           Data / Analytics
                                  |
                                  v
                              AI Layer
```

---

# 8. Control Plane vs Media Plane

This separation is one of the most important architecture decisions.

## 8.1 Control Plane

Responsible for:

- Authentication.
- Authorization.
- User/team management.
- Session creation.
- Device registration.
- Destination configuration.
- Stream metadata.
- Moderation permissions.
- Analytics metadata.
- Billing/subscription later.
- Audit logs.

Recommended stack:

- .NET Core / ASP.NET Core.
- PostgreSQL or another transactional relational database.
- Redis for caching and ephemeral state.
- SignalR/WebSocket for real-time control/status.
- Background workers for asynchronous jobs.

## 8.2 Media Plane

Responsible for:

- Video ingest.
- Audio ingest.
- Transcoding.
- Packaging.
- Distribution.
- Recording.
- Relay/fan-out to destinations.
- Media health metrics.

Do not put video packets through the normal business API.

---

# 8A. Core Product Decision — No OBS Dependency

This decision is mandatory for product design and implementation:

> **The platform must be able to broadcast a live stream by itself. OBS is optional.**

## Default flow

```text
Browser / Mobile App
        |
 Camera + Microphone + Screen
        |
   Native Live Studio
        |
   Streaming Engine
        |
 +------+------+------+
 |      |      |      |
Own   YouTube Facebook TikTok
App
```

## Optional professional flow

```text
OBS / External Encoder
          |
      RTMP / SRT
          |
   Your Streaming Engine
          |
      Distribution
```

External encoders must plug into the same Live Session model. They must not create a separate architecture.

## Acceptance criteria

- A standard creator can stream from the web app without installing OBS.
- A mobile creator can stream from the mobile app without installing OBS.
- The control room can configure and start external destinations from the same session.
- Multi-device camera workflows do not require OBS.
- OBS/encoder users can connect later as an optional advanced path.

---

# 9. Protocol Strategy

## 9.1 RTMP / RTMPS

Use for:

- OBS.
- Desktop encoders.
- External streaming destinations.
- Broad compatibility.

## 9.2 SRT

Use where appropriate for:

- Professional contribution.
- Unstable networks.
- Higher reliability over challenging links.

## 9.3 WebRTC

Use for:

- Ultra-low-latency browser experiences.
- Remote guests.
- Device contribution where appropriate.
- Interactive sessions.

## 9.4 HLS / Low-Latency HLS

Use for:

- Scalable playback.
- CDN distribution.
- Large viewer audiences.

### Recommended Flow

```text
Camera / Encoder
      |
      +--> RTMP / SRT / WebRTC
                     |
                     v
               Ingest Gateway
                     |
                     v
               Media Pipeline
                     |
          +----------+----------+
          |                     |
       Transcode             Record
          |
          +----------+----------+
                     |
                  Package
                     |
          +----------+----------+
          |                     |
        CDN              External Relays
          |                     |
       Viewers           YT / FB / TikTok / etc.
```

---

# 10. Media Processing Pipeline

A live stream should pass through these conceptual stages:

```text
INGEST
  -> VALIDATE
  -> NORMALIZE
  -> TRANSCODE
  -> AUDIO PROCESS
  -> VIDEO PROCESS
  -> PACKAGE
  -> RECORD
  -> DISTRIBUTE
  -> ANALYZE
```

### Example Transcoding Ladder

Use configurable profiles rather than hard-coding a single output.

Example:

```text
1080p60
1080p30
720p30
540p30
480p30
```

Exact bitrate and codec settings should be determined by device capability, target destination, network conditions, storage cost, and workload.

---

# 11. Smooth Streaming / Network Resilience

Reliability is a first-class product feature.

## 11.1 Client-Side Network Monitoring

Monitor:

- Upload bandwidth.
- RTT.
- Packet loss.
- Connection state.
- Jitter where measurable.
- Encoder health.
- Camera availability.
- Audio availability.

## 11.2 Adaptive Quality

```text
Network Healthy
      |
      v
Higher Profile
      |
      v
Network Degrades
      |
      v
Lower Profile
      |
      v
Connection Recovers
      |
      v
Gradual Quality Increase
```

Avoid aggressive oscillation between quality levels.

Use hysteresis and minimum hold periods.

## 11.3 Reconnection

```text
Connection Lost
      |
      v
Keep Session Alive
      |
      v
Retry with Backoff
      |
      +---- Success ---> Resume
      |
      +---- Failure ---> Continue Retry / Notify
```

The live session should have a distinction between:

- `LIVE`
- `DEGRADED`
- `RECONNECTING`
- `ENDED`
- `FAILED`

Do not immediately convert a temporary network outage into `ENDED`.

---

# 12. Session State Machine

```text
DRAFT
  |
  v
READY
  |
  v
STARTING
  |
  v
LIVE
  |
  +------> DEGRADED
  |            |
  |            v
  |        RECOVERING
  |            |
  |            v
  +---------- LIVE
  |
  v
STOPPING
  |
  v
ENDED
```

A separate `FAILED` state can be used when startup or recovery permanently fails.

---

# 13. Live Control Center UI

## 13.1 Main Layout

```text
+----------------------------------------------------------------+
| LIVE CONTROL CENTER                         Session: #LS-000123 |
+----------------------------------------------------------------+
|                                                                |
|                 LIVE PREVIEW                                   |
|                                                                |
|                                                                |
+-----------------------------+----------------------------------+
| Scene / Source              | Stream Health                    |
|                             |                                  |
| Camera 1                    | Upload       8.2 Mbps           |
| Camera 2                    | Bitrate      5.0 Mbps           |
| Screen                      | FPS          30                 |
| Guest                       | Dropped      0.2%               |
|                             | Latency      4.8 sec            |
+-----------------------------+----------------------------------+
| Destinations                                                   |
| YouTube       LIVE     Healthy                                 |
| Facebook      LIVE     Healthy                                 |
| TikTok        LIVE     Degraded                                |
| Custom RTMP   OFF      --                                      |
+----------------------------------------------------------------+
| Chat | Viewers | Analytics | Scenes | Devices | AI Assistant   |
+----------------------------------------------------------------+
```

## 13.2 Primary Controls

- Start stream.
- Stop stream.
- Pause where supported by the architecture/workflow.
- Mute/unmute.
- Camera switch.
- Scene switch.
- Add source.
- Add destination.
- Invite guest.
- Open moderator panel.
- Enable AI assistant.
- Record status.

---

# 14. Scene System

Professional users should eventually be able to create scenes.

Example:

```text
Scene 1 — Main Host
- Camera 1
- Logo
- Lower Third

Scene 2 — Product
- Product Camera
- Product Info
- Price

Scene 3 — Guest
- Guest Video
- Host Small Window

Scene 4 — Screen Share
- Screen
- Host Camera
```

Scene model:

```text
Scene
  -> Layers
      -> VideoSource
      -> Image
      -> Text
      -> Shape
      -> Audio
      -> Web overlay
```

---

# 15. Multi-Camera / Multi-Device Architecture

A device should not directly control another device's media stream without authorization.

Instead:

```text
Device A
   |
   v
Contribution Gateway
   |
   +----------------+
                    |
Device B ---> Session Router
                    |
Device C ------------+
                    |
                    v
              Production Layer
```

Each source receives:

- `sourceId`
- `deviceId`
- `sessionId`
- `role`
- `status`
- `health`
- `permissions`

The control layer sends commands such as:

```json
{
  "sessionId": "LS-000123",
  "command": "SWITCH_SOURCE",
  "sourceId": "camera-02"
}
```

Real-time commands should use SignalR/WebSocket or an equivalent channel, not polling for every event.

---

# 16. Remote Guest System

Future feature for interviews, webinars, podcasts, and live shows.

Flow:

```text
Host creates guest invite
        |
        v
Guest opens link
        |
        v
Browser permission
        |
        v
WebRTC contribution
        |
        v
Guest Gateway
        |
        v
Live Session
```

Guest permissions should include:

- Camera.
- Microphone.
- Screen share.
- Chat access.
- Leave session.

The host controls whether guest audio/video goes live.

---

# 17. Chat and Moderation

Chat should be normalized into a unified internal model.

```text
ChatMessage
- id
- sessionId
- platform
- platformMessageId
- userId
- displayName
- message
- timestamp
- moderationStatus
- sentimentScore (future)
- priorityScore (future)
```

Support:

- Unified chat.
- Delete/hide where platform APIs allow.
- Block/mute where platform APIs allow.
- Pin.
- Highlight.
- Moderator roles.
- Spam filtering.
- Keyword rules.
- AI moderation later.

---

# 18. Live Analytics

## 18.1 Real-Time Metrics

Track:

- Current viewers.
- Peak viewers.
- Average watch time.
- Concurrent viewers.
- Chat rate.
- Engagement events.
- Reactions where available.
- Stream bitrate.
- FPS.
- Dropped frames.
- Encoder load.
- Network health.
- Latency.
- Destination health.

## 18.2 Session Analytics Model

```text
LiveSessionMetric
- sessionId
- timestamp
- viewers
- bitrate
- fps
- droppedFrames
- latency
- chatRate
- sourceHealth
- destinationHealth
```

Store high-frequency operational metrics separately from core transactional data.

Suggested approach:

- PostgreSQL for transactional data.
- Redis for short-lived real-time state.
- Time-series/analytics store for high-volume metrics when scale requires it.

---

# 19. Cloud Recording

Recording should be treated as a first-class capability.

```text
LIVE SESSION
     |
     +----> Live Distribution
     |
     +----> Master Recording
     |
     +----> Proxy Recording
     |
     +----> Audio Track
     |
     +----> Transcript
```

Store recorded media in object storage.

Example lifecycle:

```text
RECORDING_STARTED
      |
      v
UPLOADING / FINALIZING
      |
      v
READY
      |
      +--> Generate thumbnails
      +--> Generate transcript
      +--> Generate clips
      +--> Index for search
```

---

# 20. AI Layer — Future Smart Streaming

AI should not be tightly coupled to the first streaming MVP.

Build a separate AI orchestration layer.

```text
Live Events
    |
    v
AI Event Bus
    |
    +--> Speech-to-Text
    +--> Chat Analyzer
    +--> Highlight Detector
    +--> Clip Generator
    +--> Moderation
    +--> Translation
    +--> AI Director
    +--> Business Assistant
```

---

# 21. AI Live Assistant

The AI assistant can appear inside the control center.

Example:

```text
AI LIVE ASSISTANT

Viewers are repeatedly asking:
"How much is this product?"

Suggested answer:
"The current price is $29.99."

[Edit] [Send]
```

Potential capabilities:

- Answer repeated questions.
- Summarize chat.
- Identify urgent issues.
- Suggest host responses.
- Detect frequently asked topics.
- Translate chat.
- Recommend when to switch scene.
- Generate post-live summary.

---

# 22. AI Auto Clipping

Input:

- Video stream.
- Audio.
- Transcript.
- Chat spikes.
- Viewer spikes.
- Reactions.
- Scene changes.

Processing:

```text
Stream
  |
  v
Transcript + Vision + Engagement Signals
  |
  v
Moment Scoring
  |
  +--> Important
  +--> Funny
  +--> Emotional
  +--> Product Demo
  +--> Educational
  +--> High Engagement
  |
  v
Clip Candidates
  |
  v
Auto Reframe
  |
  v
Captions
  |
  v
Export
```

Outputs:

- 9:16.
- 16:9.
- 1:1.
- 30 seconds.
- 60 seconds.
- 90 seconds.

---

# 23. AI Director

Future premium capability.

The AI Director can use rules + AI rather than unrestricted model decisions.

Examples:

```text
IF speaker = guest
THEN suggest guest scene

IF product detected
AND host explains product
THEN suggest product camera

IF audience engagement spikes
THEN suggest highlight marker
```

Important: AI should initially **recommend** actions rather than automatically taking control of a live broadcast. Automatic execution can be enabled later with explicit user settings and safe guardrails.

---

# 24. AI Translation and Accessibility

Future support:

- Live transcript.
- Captions.
- Translation.
- Multi-language subtitles.
- Text summaries.
- Speaker identification.
- Accessibility metadata.

Example:

```text
Host speaks English
        |
        v
Speech-to-Text
        |
        +--> English captions
        +--> Khmer translation
        +--> Thai translation
        +--> Chinese translation
```

Translation should be asynchronous or low-latency according to the event requirement and compute budget.

---

# 25. AI Post-Live Automation

After a stream ends:

```text
Stream End
    |
    +--> Summary
    +--> Chapters
    +--> Transcript
    +--> Highlights
    +--> Short Clips
    +--> Social Captions
    +--> Thumbnail Concepts
    +--> Engagement Report
    +--> FAQ Extraction
```

---

# 26. Backend Services

A recommended logical service structure:

```text
API Gateway
 |
 +-- Identity Service
 +-- User/Team Service
 +-- Live Session Service
 +-- Device Service
 +-- Destination Service
 +-- Chat Service
 +-- Analytics Service
 +-- Recording Service
 +-- Notification Service
 +-- AI Orchestrator
 +-- Billing Service (later)
```

The first release does not need to deploy every logical service as a separate microservice.

### Recommended initial approach

Start as a modular monolith for the control plane where practical, while keeping the media plane separate.

This reduces operational complexity during early development.

Split services when:

- independent scaling is required,
- deployment cadence differs,
- ownership boundaries become clear,
- failures need stronger isolation.

---

# 27. Suggested Technology Stack

## Frontend

- Next.js.
- TypeScript.
- React.
- Tailwind CSS or an established component system.
- WebRTC APIs where needed.
- SignalR/WebSocket client.

## Mobile

Option A:

- React Native.

Option B for media-heavy requirements:

- Native Android/iOS modules for camera/encoder functionality integrated into the React Native app.

Do not assume all high-performance camera/encoding features can be implemented safely in pure JavaScript.

## Backend

- ASP.NET Core / .NET.
- C#.
- REST APIs for standard business operations.
- SignalR for real-time session/control events.

## Database

- PostgreSQL for primary transactional storage.
- Redis for cache, locks, presence, ephemeral session state, and rate limiting.

## Media

Possible implementation components:

- FFmpeg for processing/transcoding workloads.
- A dedicated media server/gateway technology for WebRTC and real-time media.
- Object storage for recordings.
- CDN for viewer delivery.

The exact media server should be selected based on required scale, protocol coverage, operational expertise, and licensing.

## Infrastructure

- Docker.
- Kubernetes later when scale and operations justify it.
- Managed database.
- Managed object storage.
- Managed CDN.
- Centralized logging.
- Metrics and tracing.

---

# 28. API Design

## 28.1 Create Session

`POST /api/live-sessions`

```json
{
  "title": "Product Launch",
  "description": "New product presentation",
  "visibility": "PUBLIC",
  "recordingEnabled": true
}
```

Response:

```json
{
  "id": "LS-000123",
  "status": "READY",
  "createdAt": "2026-08-30T00:00:00Z"
}
```

## 28.2 Create Ingest Credential

`POST /api/live-sessions/{id}/ingest-credentials`

Response concept:

```json
{
  "protocol": "RTMP",
  "serverUrl": "rtmps://ingest.example.com/live",
  "streamKey": "opaque-secret"
}
```

Do not expose stream keys unnecessarily. Store/handle them as secrets.

## 28.3 Connect Device

`POST /api/live-sessions/{id}/devices`

```json
{
  "deviceName": "Phone Camera 2",
  "role": "CAMERA"
}
```

## 28.4 Add Destination

`POST /api/live-sessions/{id}/destinations`

```json
{
  "provider": "CUSTOM_RTMP",
  "name": "Partner Stream",
  "endpointUrl": "rtmps://example.com/live"
}
```

Secrets should be referenced through secure secret storage rather than returned or logged unnecessarily.

## 28.5 Start Session

`POST /api/live-sessions/{id}/start`

## 28.6 Stop Session

`POST /api/live-sessions/{id}/stop`

## 28.7 Get Health

`GET /api/live-sessions/{id}/health`

---

# 29. Real-Time Event Model

SignalR/WebSocket events can include:

```text
session.status.changed
source.connected
source.disconnected
source.health.changed
destination.status.changed
stream.health.changed
viewer.count.changed
chat.message.created
chat.moderation.changed
scene.changed
recording.status.changed
ai.insight.created
```

Example:

```json
{
  "event": "destination.status.changed",
  "sessionId": "LS-000123",
  "destinationId": "dest-02",
  "status": "DEGRADED",
  "reason": "Upstream reconnecting"
}
```

---

# 30. Data Model

Core entities:

```text
User
Organization
Team
Role
Permission
LiveSession
LiveSource
Device
Scene
SceneLayer
Destination
Recording
ChatMessage
LiveMetric
ModerationAction
Notification
AIJob
AIInsight
Clip
Transcript
AuditLog
```

Relationships:

```text
Organization
  |
  +-- Users
  +-- Teams
       |
       +-- LiveSessions
              |
              +-- Devices
              +-- Sources
              +-- Scenes
              +-- Destinations
              +-- Recordings
              +-- Messages
              +-- Metrics
              +-- AIJobs
              +-- Clips
```

---

# 31. Security

Security must be designed from Phase 1.

## Authentication

- Access tokens.
- Refresh tokens where applicable.
- Session/device credentials.
- MFA later.
- SSO later.

## Authorization

Roles:

- Owner.
- Admin.
- Producer.
- Host.
- Moderator.
- Analyst.
- Viewer.

Permissions should support resource scope.

Example:

```text
LIVE_SESSION_VIEW
LIVE_SESSION_EDIT
LIVE_SESSION_START
LIVE_SESSION_STOP
DESTINATION_MANAGE
CHAT_MODERATE
ANALYTICS_VIEW
RECORDING_VIEW
AI_USE
```

## Secrets

Never log:

- Stream keys.
- OAuth refresh tokens.
- API client secrets.
- Destination credentials.

Use a secret manager when production scale requires it.

---

# 32. Abuse Prevention and Safety Controls

Include:

- Rate limiting.
- Stream creation limits.
- Chat rate limits.
- Destination limits.
- Device/session limits.
- Audit logs.
- Content reporting hooks.
- Moderator controls.
- Fraud/abuse monitoring.
- Storage quotas.

---

# 32A. Product Non-Goals for MVP

The first release should **not** require the team to recreate every feature of OBS. The product should focus on a dependable native broadcaster and a clean live workflow.

Do not block MVP on:

- Full plugin compatibility.
- Every OBS scene/plugin feature.
- Advanced broadcast hardware control.
- Studio-grade effects that do not improve the core streaming workflow.

These can be introduced after native streaming reliability is proven.

---

# 33. Observability

Every live stream should be observable.

## Metrics

- Ingest success rate.
- Stream start time.
- Stream start failure rate.
- Reconnect frequency.
- Destination failures.
- Transcoding errors.
- Dropped frames.
- Egress traffic.
- Viewer latency.
- Recording failures.
- API latency.
- WebSocket/SignalR connection count.

## Logs

Use structured logs with:

- `timestamp`
- `level`
- `service`
- `sessionId`
- `deviceId`
- `destinationId`
- `requestId`
- `errorCode`

Never log secrets.

## Tracing

Use distributed tracing across:

```text
Client
 -> API
 -> Session Service
 -> Media Control
 -> Destination
 -> Analytics
```

---

# 34. Error Handling Strategy

Use stable internal error codes.

Examples:

```text
LIVE_001_SESSION_NOT_FOUND
LIVE_002_SESSION_NOT_READY
LIVE_003_STREAM_START_FAILED
LIVE_004_DESTINATION_AUTH_FAILED
LIVE_005_SOURCE_DISCONNECTED
LIVE_006_NETWORK_DEGRADED
LIVE_007_RECORDING_FAILED
LIVE_008_PERMISSION_DENIED
```

User-facing errors should be clear and actionable.

Bad:

> `500 Internal Server Error`

Better:

> `YouTube connection expired. Reconnect YouTube before starting the stream.`

---

# 35. Billing / SaaS Model (Future)

Potential plans:

### Free

- Limited sessions.
- Limited recording.
- Basic destination support.

### Creator

- Longer sessions.
- More recording storage.
- More destinations.
- Basic AI tools.

### Pro

- Multi-camera.
- Advanced analytics.
- Professional scenes.
- More concurrent destinations.
- AI clipping.

### Business

- Teams.
- Roles.
- Multiple operators.
- Higher limits.
- Brand customization.

### Enterprise

- SSO.
- Audit.
- Custom retention.
- Regional deployment.
- Enterprise support.

Billing should be based on clearly measurable resource drivers such as:

- Streaming minutes.
- Recording/storage.
- Transcoding usage.
- Egress.
- AI processing.
- Number of destinations.

---

# 36. Phase Roadmap

## Phase 0 — Architecture & Validation

Goal: validate the hardest media requirements before building the whole product.

Deliverables:

- Media architecture decision.
- Protocol decision.
- Proof of concept.
- One ingest source.
- One browser/player.
- One recording path.
- One external destination.
- Basic monitoring.

Success criteria:

- Stable stream for repeated test sessions.
- Reconnect works.
- Recording is recoverable.
- Metrics can be observed.

---

## Phase 1 — MVP Core Live Streaming

Build:

- Authentication.
- User profile.
- Live session CRUD.
- RTMP ingest.
- Live player.
- Start/stop stream.
- Stream key.
- Recording.
- Basic health dashboard.
- Basic viewer count.
- Basic SignalR real-time updates.
- One external destination adapter.
- Error handling.

### Phase 1 UI

Screens:

1. Login.
2. Dashboard.
3. Live Sessions.
4. Create Session.
5. Live Control Center.
6. Recordings.
7. Destinations.
8. Basic Analytics.

### Phase 1 should NOT attempt

- Full AI Director.
- Complex scene editor.
- Advanced multi-camera mixing.
- Dozens of external platforms.
- Global multi-region architecture.

---

# 37. Phase 2 — Multi-Platform Distribution

Add:

- Provider adapter architecture.
- YouTube integration.
- Facebook integration where supported/approved.
- TikTok integration where the applicable APIs/account capabilities are available.
- Twitch integration.
- Custom RTMP.
- Destination health.
- Automatic retry.
- OAuth-based destination management where applicable.

Key outcome:

```text
ONE SESSION
   |
   +--> YouTube
   +--> Facebook
   +--> TikTok
   +--> Twitch
   +--> Custom RTMP
```

---

# 38. Phase 3 — Multi-Device / Multi-Camera

Add:

- Device pairing.
- Phone camera contribution.
- Multiple camera sources.
- Source preview.
- Remote scene switching.
- Device health.
- Guest links.
- Browser WebRTC contribution.
- Screen sharing.

Key outcome:

```text
Phone A -----+
Phone B -----+
Laptop ------+----> Live Session
Guest -------+
Camera ------+
```

---

# 39. Phase 4 — Professional Live Studio

Add:

- Scene editor.
- Overlays.
- Lower thirds.
- Logo.
- Product cards.
- Audio mixer.
- Picture-in-picture.
- Screen layouts.
- Guest layout.
- Transition system.
- Stream presets.
- Reusable production templates.

---

# 40. Phase 5 — AI Live Intelligence

Add:

- Live transcription.
- AI chat assistant.
- Smart moderation.
- FAQ detection.
- Live insights.
- Highlight markers.
- Automatic clips.
- Smart captions.
- Translation.
- AI recommendations.

Start with **assistive AI**.

Example:

```text
AI recommends action
       |
       v
Human approves
       |
       v
Action executes
```

Then selectively introduce automation where reliability is proven.

---

# 41. Phase 6 — AI Producer / Automation

Add:

- AI Director.
- Automatic scene recommendations.
- Smart camera switching.
- Automated clipping.
- Automated post-live publishing workflows.
- Intelligent engagement optimization.
- Automated stream summaries.
- Automated content packages.

Use deterministic rules as guardrails around AI behavior.

---

# 42. Phase 7 — Global Scale

Only after product-market fit and measurable load requirements.

Add:

- Multi-region ingest.
- Regional routing.
- Global CDN strategy.
- Multi-region failover.
- Autoscaling.
- Capacity forecasting.
- Regional data policies.
- Disaster recovery.
- Advanced cost optimization.

Architecture:

```text
                   Global DNS
                       |
        +--------------+--------------+
        |                             |
      Region A                      Region B
        |                             |
    Ingest Cluster                Ingest Cluster
        |                             |
    Media Cluster                 Media Cluster
        |                             |
        +--------------+--------------+
                       |
                      CDN
                       |
                    Viewers
```

---

# 43. MVP Delivery Priority

Priority should be:

```text
P0
Reliability
Core ingest
Live playback
Session lifecycle
Basic monitoring

P1
Recording
One external destination
Real-time control

P2
Multiple destinations
Multi-device
Multi-camera

P3
Professional scenes
Guests
Advanced analytics

P4
AI features

P5
Global scale
```

Do not reverse this order.

---

# 44. Recommended Development Structure

## Repository

A monorepo can work well initially.

Example:

```text
live-platform/
|
+-- apps/
|   +-- web/
|   +-- mobile/
|
+-- services/
|   +-- api/
|   +-- workers/
|
+-- media/
|   +-- ingest/
|   +-- processing/
|   +-- distribution/
|
+-- packages/
|   +-- contracts/
|   +-- shared-types/
|   +-- ui/
|   +-- config/
|
+-- infrastructure/
|   +-- docker/
|   +-- terraform/
|   +-- k8s/
|
+-- docs/
```

---

# 45. Backend Module Structure

```text
API
|
+-- Auth
+-- Users
+-- Organizations
+-- Teams
+-- LiveSessions
+-- Devices
+-- Sources
+-- Scenes
+-- Destinations
+-- Recordings
+-- Chat
+-- Analytics
+-- Notifications
+-- AI
+-- Audit
```

Keep domain boundaries clear even if deployed in one application first.

---

# 46. Testing Strategy

Live systems require more than normal unit testing.

## Unit Tests

Test:

- Business rules.
- Session state machine.
- Permissions.
- Destination validation.
- Retry policies.
- Billing calculations later.

## Integration Tests

Test:

- Database.
- Redis.
- SignalR.
- Destination adapters.
- Recording workflows.

## Media Tests

Test:

- Stream startup.
- Long-running stream.
- Network loss.
- Network recovery.
- Packet loss.
- High latency.
- Device disconnect.
- Destination disconnect.
- Recording interruption.

## Load Tests

Measure:

- API requests.
- Concurrent sessions.
- Concurrent devices.
- SignalR connections.
- Viewer traffic.
- Media processing utilization.
- Database performance.
- Redis performance.

---

# 47. Network Test Matrix

Create repeatable tests for:

```text
Excellent network
Good network
Weak 4G
Poor 4G
Wi-Fi congestion
Packet loss
High RTT
Temporary disconnect
Repeated disconnect
Bandwidth recovery
```

A platform marketed as "smooth" should prove this through repeatable test scenarios rather than visual demos only.

---

# 48. Key Reliability Targets

Set targets during implementation rather than using vague statements.

Examples of engineering targets:

- Stream startup success rate.
- Reconnection success rate.
- Destination delivery success rate.
- Recording completion rate.
- Control-plane availability.
- Maximum acceptable recovery time.
- Maximum acceptable viewer interruption.
- Error rate per 1,000 sessions.

Exact SLO values should be selected after MVP load testing and business requirements are known.

---

# 49. Cost Control

Video workloads can become expensive quickly.

Major cost drivers:

- Transcoding.
- Egress.
- Storage.
- CDN.
- AI processing.
- Long-running sessions.
- Multiple output variants.

Optimization strategies:

- Avoid unnecessary transcoding.
- Use source passthrough where safe.
- Generate only necessary profiles.
- Delete temporary files promptly.
- Use lifecycle policies for storage.
- Cache metadata, not large media in Redis.
- Track media cost per session.
- Track AI cost per feature.

---

# 50. Smart Architecture Rules

## Rule 1 — Never couple the UI directly to the media server internals

The UI should call a stable control API.

## Rule 2 — Treat external platforms as adapters

Do not write platform-specific logic into the core live-session engine.

## Rule 3 — Treat temporary network failures as recoverable states

Do not end a session because a phone loses connectivity for a few seconds.

## Rule 4 — Separate control-plane scaling from media-plane scaling

The API and media workloads scale differently.

## Rule 5 — Keep AI outside the critical path initially

The stream must remain live if an AI service is unavailable.

## Rule 6 — Make every action observable

Start, stop, reconnect, destination failure, source failure, recording failure, and moderation actions should all emit measurable events.

## Rule 7 — Prefer configuration over hard-coded provider behavior

New platforms and streaming profiles should be added through adapters/configuration where practical.

---

# 51. First Release User Journey

```text
User logs in
   |
   v
Create Live Session
   |
   v
Choose Source
   |
   +--> Browser Camera / Mic  (default)
   +--> Screen Share
   +--> Mobile Camera Device
   +--> Optional OBS / RTMP / SRT
   |
   v
Choose Destination
   |
   +--> Your Platform
   +--> YouTube
   |
   v
Preflight Check
   |
   +--> Camera OK
   +--> Microphone OK
   +--> Network OK
   +--> Destination OK
   |
   v
Start Live
   |
   v
Monitor Health
   |
   v
Stop Live
   |
   v
Recording Processing
   |
   v
Replay + Analytics
```

---

# 52. Preflight Check

Before starting a live stream, automatically check:

- Camera available.
- Microphone available.
- Permissions.
- Network upload estimate.
- Destination authentication.
- Stream profile.
- Storage/recording availability.
- Device temperature/performance where accessible.
- Browser support where applicable.

Example UI:

```text
READY TO GO LIVE

Camera          ✓
Microphone      ✓
Network         ✓ Good
Recording       ✓
YouTube         ✓ Connected

Estimated quality: 1080p30

[ START LIVE ]
```

---

# 53. Future Product Extensions

Once the foundation is stable, the platform can expand into:

- Live shopping.
- Webinar platform.
- Online events.
- Paid live sessions.
- Ticketed streams.
- Memberships.
- Donations/tips.
- Product checkout during stream.
- Lead capture.
- CRM integrations.
- Event registration.
- Course/live education.
- Enterprise town halls.
- Internal company broadcasts.
- Sports/event streaming where rights permit.

These should be built as modules around the same Live Session foundation.

---

# 54. Definition of Done — Phase 1

Phase 1 is ready only when all of the following work reliably:

- User can create a session.
- User can obtain an ingest credential.
- User can stream from OBS/encoder.
- Platform receives the stream.
- Platform displays the live video.
- User can start and stop the session.
- Session state updates in real time.
- Temporary connection loss is detectable.
- Recording is generated successfully.
- Basic analytics are visible.
- One external destination can be configured and tested.
- Errors are visible and actionable.
- Secrets are not exposed in logs.
- Automated tests cover critical control-plane logic.
- Basic media reliability tests pass.

---

# 55. Definition of Done — Multi-Platform

- Multiple destinations can exist under one session.
- Each destination has independent status.
- One failing destination does not automatically kill other destinations.
- Destination retry works.
- Provider credentials are securely managed.
- Destination disconnect is visible immediately.
- Adding a new provider does not require changes to core session logic.

---

# 56. Definition of Done — Multi-Device

- Devices can join a session securely.
- Device roles are enforced.
- Device status is visible.
- Multiple sources can coexist.
- Host can select an active source.
- A disconnected source does not terminate the session.
- Remote commands are delivered reliably.
- Device reconnect behavior is tested.

---

# 57. Definition of Done — AI

AI features are considered production-ready only when:

- AI cannot accidentally terminate a live session without explicit authorization.
- AI failure cannot stop the stream.
- User can override AI actions.
- AI recommendations show enough context for human review.
- AI costs are measurable.
- AI outputs are logged appropriately.
- Sensitive data handling is defined.
- Model/provider changes do not require rewriting the live-media core.

---

# 58. Recommended Build Order

The exact implementation sequence should be:

```text
1. Architecture proof of concept
2. Live session model
3. RTMP ingest
4. Live playback
5. Session lifecycle
6. Recording
7. Real-time health
8. One destination adapter
9. Multi-destination abstraction
10. Mobile contribution
11. Multi-device control
12. WebRTC guest flow
13. Scene system
14. Advanced analytics
15. AI assistant
16. AI clipping
17. AI director
18. Global scaling
```

---

# 59. Important Architectural Decision

The platform should **not** be designed around the assumption that every feature must live inside the .NET backend.

A clean boundary is:

```text
.NET / Application Layer
       |
       | control
       v
Media Infrastructure
       |
       +--> Ingest
       +--> Transcoding
       +--> Packaging
       +--> Recording
       +--> Distribution
```

The .NET application manages the business and control plane.

The media layer manages high-throughput real-time media.

This separation will make the product significantly easier to scale and maintain.

---

# 60. Final Recommended Product Architecture

```text
                         USERS
                           |
          +----------------+----------------+
          |                |                |
       Web App          Mobile App       OBS/Encoder
          |                |                |
          +----------------+----------------+
                           |
                           v
                    API / Gateway
                           |
          +----------------+----------------+
          |                |                |
       Identity        Live Session      Teams/Roles
          |                |                |
          +----------------+----------------+
                           |
                    REAL-TIME CONTROL
                         SignalR
                           |
                           v
                   MEDIA ORCHESTRATOR
                           |
             +-------------+-------------+
             |             |             |
           Ingest      Transcoding    Recording
             |             |             |
             +-------------+-------------+
                           |
                     DISTRIBUTION
                           |
           +---------------+----------------+
           |               |                |
          CDN           External         Storage
        Viewers         Platforms
                           |
          +----------------+----------------+
          |                |                |
       YouTube          Facebook         TikTok
                                            |
                                            v
                                      More Adapters

                           |
                           v
                       AI LAYER
                           |
       +-------------------+--------------------+
       |                   |                    |
  AI Assistant       AI Moderation        AI Clips
       |                   |                    |
       +-------------------+--------------------+
                           |
                      AI Director
                           |
                           v
                    Smart Automation
```

---

# 61. Recommended Immediate Implementation Scope

For the first engineering cycle, implement **Phase 1 only**, but structure the code so all later phases fit the same abstractions.

### Build now

- Next.js control dashboard.
- ASP.NET Core control API.
- PostgreSQL database.
- Redis.
- SignalR.
- Live Session model.
- RTMP ingest path.
- Live playback.
- Recording.
- Stream health.
- One destination adapter.
- Audit/error logging.
- Dockerized local development.
- Basic CI pipeline.

### Design now, implement later

- Destination provider interface.
- Multi-device source model.
- Scene model.
- Guest model.
- AI job/event model.
- Analytics event schema.
- Organization/team/role model.

This prevents future features from forcing a rewrite while keeping the MVP small enough to ship.

---

# 62. Engineering Principle for the Whole Product

The platform should evolve in this direction:

```text
Simple Streaming
      |
      v
Reliable Streaming
      |
      v
Multi-Platform Streaming
      |
      v
Multi-Device Production
      |
      v
Professional Live Studio
      |
      v
AI-Assisted Live Production
      |
      v
AI-Driven Live Operating Platform
```

The long-term goal is not merely **"stream video"**.

The long-term goal is:

> **Give a user one live session that intelligently manages sources, platforms, production, reliability, audience interaction, analytics, recordings, and content creation.**

---

# 63. Suggested Next Engineering Document Set

This blueprint should become the parent document for the implementation documentation.

Recommended child documents:

```text
01-product-requirements.md
02-system-architecture.md
03-media-architecture.md
04-database-schema.md
05-api-specification.md
06-signalr-events.md
07-mobile-streaming.md
08-multi-platform-adapters.md
09-recording-storage.md
10-analytics.md
11-security.md
12-observability.md
13-ai-architecture.md
14-phase-1-implementation.md
15-testing-strategy.md
16-deployment.md
```

The first implementation-specific document should be `14-phase-1-implementation.md`, because Phase 1 establishes the stable foundation for every later feature.

---

# Appendix A — Core Terminology

| Term | Meaning |
|---|---|
| Live Session | One logical live event containing sources, destinations, chat, recording, and analytics |
| Source | A video/audio contribution into the session |
| Device | Physical/browser device contributing to or controlling a session |
| Destination | External platform or endpoint receiving the stream |
| Ingest | Entry point where a media stream enters the platform |
| Media Plane | High-throughput real-time media processing/distribution layer |
| Control Plane | Business/control layer managing sessions and configuration |
| Scene | Visual composition of media and overlays |
| Recording | Persistent media copy of a live session |
| AI Job | Asynchronous AI processing task |
| AI Insight | AI-generated recommendation or analysis |

---

# Appendix B — Example Live Session JSON

```json
{
  "id": "LS-000123",
  "title": "Product Launch",
  "status": "LIVE",
  "visibility": "PUBLIC",
  "recordingEnabled": true,
  "sources": [
    {
      "id": "src-01",
      "type": "CAMERA",
      "deviceId": "dev-01",
      "status": "CONNECTED"
    },
    {
      "id": "src-02",
      "type": "SCREEN",
      "deviceId": "dev-02",
      "status": "CONNECTED"
    }
  ],
  "destinations": [
    {
      "id": "dest-01",
      "provider": "YOUTUBE",
      "status": "LIVE"
    },
    {
      "id": "dest-02",
      "provider": "CUSTOM_RTMP",
      "status": "LIVE"
    }
  ]
}
```

---

# Appendix C — Product Success Metrics

Track these from the beginning:

### Reliability

- Stream start success rate.
- Stream recovery success rate.
- Destination success rate.
- Recording completion rate.

### Engagement

- Sessions per user.
- Average live duration.
- Viewer retention.
- Chat rate.
- Repeat creators.

### Product Adoption

- Percentage of users connecting multiple destinations.
- Percentage using multiple devices.
- Recording usage.
- AI feature usage.

### Economics

- Cost per streaming hour.
- Cost per viewer hour.
- Storage cost per recording hour.
- AI cost per generated clip.
- Gross margin by plan.

---

# Conclusion

Build the product around a **Live Session abstraction** and a strict separation between **Control Plane**, **Media Plane**, **Distribution**, and **AI Layer**. The **native broadcaster is part of the product**, while OBS and other encoders remain optional compatibility inputs.

Start with reliable core streaming, then add multi-platform, multi-device, professional production, and AI capabilities in that order.

The architecture should make the next feature an extension rather than a rewrite.

**Target end state:**

```text
One Session
   -> Many Devices
   -> Many Cameras
   -> Many Platforms
   -> Many Viewers
   -> One Control Center
   -> One Recording
   -> One Analytics Layer
   -> One AI Intelligence Layer
```

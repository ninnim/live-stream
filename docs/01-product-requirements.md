# 01 — Product Requirements

## Product goal

Create a native-first live-streaming platform where a creator can start, control, monitor, record, and distribute a live session from the platform itself without needing OBS.

## Core user journeys

### Creator — web
1. Sign in.
2. Create Live Session.
3. Grant camera/microphone permissions.
4. Select source and quality.
5. Preview local media.
6. Start live.
7. Monitor health, viewers, chat, and destination state.
8. Stop live.
9. Access recording and analytics.

### Creator — mobile
1. Open app.
2. Create session.
3. Select front/rear camera and microphone.
4. Start live.
5. Monitor connection and chat.
6. Recover from temporary network interruptions when possible.
7. End live and access recording.

### Professional creator
Use an external encoder such as OBS through RTMP/SRT. The external encoder is treated as an input source to the same Live Session model.

## Functional requirements

- Authentication and tenant/workspace model.
- Live-session lifecycle.
- Browser camera/microphone capture.
- Browser screen sharing.
- Stream ingest.
- Live playback.
- Recording.
- Stream health monitoring.
- Chat and moderation foundation.
- Destination management.
- Multi-device session model.
- Mobile broadcasting.
- Team roles/permissions.
- AI features as later phases.

## UX principles

- One obvious Start Live action.
- Show connection health before and during broadcast.
- Never expose unnecessary infrastructure terminology to normal users.
- Errors must explain what happened and what the user can do.
- Keep the live session alive during recoverable source/network failures.
- Show destination-specific failures independently.

## Non-goals for Phase 1

- TikTok/Facebook/YouTube publishing.
- Native mobile apps.
- Multi-camera composition.
- AI director.
- Automatic clipping.
- Enterprise SSO.
- Global multi-region infrastructure.

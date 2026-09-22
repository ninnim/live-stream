# 08 — Mobile App

## Scope

Native Android and iOS broadcaster focused on fast capture and reliability.

## Required features

- Login/session authentication
- Camera preview
- Front/rear switch
- Microphone control
- Quality selection / auto mode
- Network health
- Start/stop live
- Reconnect status
- Live timer
- Basic chat moderation visibility
- Recording completion state

## Mobile constraints

- Respect OS permissions.
- Handle app lifecycle transitions explicitly.
- Do not assume background camera capture is always available.
- Preserve session identifiers securely.
- Avoid unnecessary battery/network usage.

## Architecture

Keep the mobile capture/media layer separate from business UI so future native media changes do not require rewriting the entire application.

## Implementation

Delivered in Phase 3 as the web application made phone-shaped and installable, rather than a
separate React Native project. `docs/decisions/0021-mobile-broadcasting.md` records that decision
and what it costs; `docs/implementation-notes/phase-3-report.md` records what was built and how it
was verified.

The separation this document asks for is kept: `apps/web/src/lib/media/mobile.ts` and
`apps/web/src/hooks/useMobileCapture.ts` are the mobile capture/media layer, and nothing above them
knows how a camera is opened. A native shell replaces those and keeps the rest.

Every required feature above is present except basic chat moderation visibility, which does not
exist anywhere in the platform yet, on any client.

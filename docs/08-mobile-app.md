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

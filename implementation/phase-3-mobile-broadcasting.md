# Phase 3 — Mobile Broadcasting

## Objective

Add native Android/iOS broadcasting as a first-class broadcaster.

## Scope

- React Native app or agreed native architecture.
- Authentication.
- Camera/mic capture.
- Session start/stop.
- Network-aware quality.
- Reconnect.
- Secure short-lived ingest credentials.
- Basic chat/session status.

## Acceptance criteria

A creator can start a live session from supported Android and iOS devices without OBS or desktop software.

## Status

**Implemented.** Delivered as a phone-shaped, installable broadcaster inside `apps/web`, sharing
the control plane, WHIP ingest and session aggregate with the desktop studio — the "agreed native
architecture" branch of the scope above, agreed and recorded in
`docs/decisions/0021-mobile-broadcasting.md`.

Scope, item by item:

| Scope item | Where |
| --- | --- |
| Authentication | Existing sign-in; the mobile broadcaster holds the same tokens |
| Camera/mic capture | `apps/web/src/hooks/useMobileCapture.ts`, front/back switching mid-broadcast |
| Session start/stop | `MobileStudio`, through the same state machine as the studio |
| Network-aware quality | `apps/web/src/lib/media/adaptive.ts` + `mobile.ts` |
| Reconnect | Existing `WhipPublisher` backoff, with encoder limits carried across |
| Short-lived ingest credentials | Existing per-attempt credential endpoint, unchanged |
| Session status | Server-authoritative, on the viewfinder |
| Basic chat | Not implemented — chat does not exist on any client yet |

Verification and known limits: `docs/implementation-notes/phase-3-report.md`.
Operating it: `docs/troubleshooting/mobile-broadcasting.md`.

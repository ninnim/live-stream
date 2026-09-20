# ADR 0007 — Browser token storage in the studio

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

`docs/11-security.md` requires short-lived access tokens with refresh rotation, and states that
permanent stream keys must never be used in the web client.

The studio has an unusual constraint for a web app: **a broadcast can outlive a page load.** The
Phase 1 acceptance criteria require that "refreshing the control room does not corrupt session
state". If a reload signs the broadcaster out, they cannot stop their own stream.

## Decision

Two different kinds of credential are handled two different ways.

### Streaming credentials — memory only, never stored

Ingest tokens are requested from `POST /api/v1/live-sessions/{id}/sources/credentials` for **each
connection attempt**, including every reconnect. They live in a local variable inside the WHIP
publisher for the duration of one handshake and are never written to storage, never logged, and
never placed in a URL the browser will retain.

They expire in five minutes, are scoped to one media path, and are revoked when the session stops.

### Session tokens — `localStorage`

Access and refresh tokens are stored in `localStorage` so a studio reload keeps the broadcaster
signed in.

This is the trade-off being made explicitly: `localStorage` is readable by any script running on the
origin, so it is vulnerable to XSS in a way an `HttpOnly` cookie is not.

## Why not HttpOnly cookies

They are the stronger option against XSS and are the right destination. They were not adopted in
Phase 1 because they change the shape of several things at once:

- CSRF protection becomes mandatory on every mutating endpoint;
- the studio and API are separate origins, so cookies need `SameSite=None; Secure`, and therefore
  HTTPS in local development;
- SignalR's WebSocket handshake cannot carry an `Authorization` header, so its token path would need
  reworking alongside.

That is a coherent piece of work, and doing it badly under time pressure produces a system that looks
more secure while being less so. It is recorded as a known limitation with a clear owner in Phase 2.

## Mitigations in place now

- **Access tokens live 15 minutes**, bounding the value of a stolen one.
- **Refresh tokens rotate on every use**, and replaying a rotated token revokes the whole chain — so
  theft is detectable and self-limiting.
- **No streaming credential is ever persisted**, so an XSS that reads `localStorage` still cannot
  publish to a stream without also calling the API, which is authorized and rate-limited.
- **Every storage access is wrapped in try/catch**, so private-browsing modes and blocked storage
  degrade to a session that works for one page load rather than crashing.
- **Next.js `poweredByHeader` is off** and React escapes interpolated content by default.

## Consequences

- An XSS vulnerability in the studio would expose session tokens. Reducing that risk is a Phase 2
  task, tracked as a known limitation.
- The API needs no CSRF machinery in Phase 1, because it authenticates with a bearer header rather
  than an ambient cookie.

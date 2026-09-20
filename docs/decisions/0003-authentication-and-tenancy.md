# ADR 0003 — Authentication and the workspace tenancy model

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

The Phase 1 prompt says to integrate with "existing auth". The repository contained no code at all,
so there was nothing to integrate with and the smallest clean component had to be built instead.

`docs/11-security.md` requires short-lived access tokens, refresh rotation, and server-side
authorization on every session mutation. `docs/10-database-design.md` specifies `users`,
`workspaces`, and `workspace_members`. `MASTER_BLUEPRINT.md` §31 lists six roles.

## Decision

Build minimal first-party authentication:

- **Password hashing** via ASP.NET Core's `PasswordHasher<T>` (PBKDF2-HMAC-SHA512, per-password salt,
  constant-time verification), taken from `Microsoft.Extensions.Identity.Core` **without** adopting
  the full ASP.NET Core Identity stack. Identity brings user stores, claims management, two-factor
  scaffolding, and UI that Phase 1 does not need — a large framework for a small problem.
- **Access tokens**: JWT, 15-minute lifetime, carrying only `sub`, `email`, and `jti`.
- **Refresh tokens**: opaque 256-bit random values, stored only as SHA-256 hashes, rotated on every
  use. Presenting an already-rotated token revokes the entire chain, which is the standard detection
  for replay of a stolen token.
- **Tenancy**: every user gets a workspace on registration. Every Live Session belongs to exactly one
  workspace, and all access is decided by workspace membership.
- **Authorization**: a single role-to-permission table (`WorkspacePermissions`), consulted through
  one service (`LiveSessionAuthorizationService`) that every session mutation routes through.

All six blueprint roles exist now even though Phase 1 only exercises a few. Adding a role later would
otherwise be a permission migration across every endpoint.

## Notable details

**Non-membership and insufficient role produce the same error.** Both return
`LIVE_008_PERMISSION_DENIED` / HTTP 403. Returning 404 for one and 403 for the other would let a
caller probe which session identifiers exist in other workspaces.

**List endpoints filter at the query level.** `LiveSessionService.ListAsync` restricts to the
caller's workspaces inside the SQL query rather than filtering results afterwards, so there is no
post-filter step that a future change could forget.

**The JWT issuer uses the system clock, not the injectable `IClock`.** Token lifetimes are validated
by the JWT middleware against the system clock; issuing against a different time source would produce
tokens rejected as not-yet-valid or already-expired. The injectable clock remains in use everywhere
the *platform* owns the meaning of time — credential expiry, recovery windows, session duration.

## Consequences

- No SSO, MFA, or email verification in Phase 1. All are later-phase items and slot in behind the
  same `AuthService` surface.
- Workspace member management has no API yet: a workspace has exactly one member, its owner. The
  authorization model is fully implemented and tested, so adding invitations later is additive.
- Tokens are held in browser `localStorage`. This is a deliberate, documented trade-off — see
  `docs/decisions/0007-browser-token-storage.md`.

# 11 — Security

## Identity

- Short-lived access tokens.
- Refresh token rotation where applicable.
- Server-side authorization on every session mutation.

## Session isolation

A user can only access sessions allowed by workspace membership and role.

## Ingest credentials

- Never use permanent stream keys in the web client.
- Issue short-lived, scoped credentials.
- Revoke credentials when session/device access is revoked.

## Destination credentials

- Encrypt at rest.
- Keep provider tokens out of API responses and frontend state.
- Log provider IDs, not secrets.

## Abuse controls

- Rate-limit session creation.
- Rate-limit pairing and credential issuance.
- Limit concurrent sessions by plan/tenant.
- Protect chat and public endpoints from spam.

## Audit

Record sensitive actions such as:

- stream start/stop
- destination connect/disconnect
- device pair/revoke
- role/permission changes
- credential rotation
- recording deletion

## Privacy

Minimize personal data collection. Provide deletion/retention controls in later phases.

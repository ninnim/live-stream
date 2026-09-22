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

### Encoder stream keys

External encoders cannot run the browser's per-connection handshake: the key is typed in by hand
and re-presented on every reconnect, so it must outlive a connection. The rule above still holds —
the key is not permanent and never reaches the web client as a reusable secret for the *browser*
path — but it is deliberately longer-lived, and the compensating controls are what make it
acceptable (ADR 0022):

- Bound to one session's media path, so it cannot publish into another session.
- Returned exactly once; only a hash is stored, so it cannot be read back.
- Rotatable, and issuing a new key revokes the previous one immediately.
- Revocable independently of browser credentials.
- Dead when the session ends, whatever its remaining lifetime says.
- Capped by configuration at one week, so it cannot be turned into a permanent secret.

RTMP ingest is off unless configured, and is unencrypted when on. A deployment carrying encoder
traffic across the internet should front it with RTMPS or prefer SRT.

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

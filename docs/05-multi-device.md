# 05 — Multi-Device

## Goal

Allow multiple authorized devices to participate in one Live Session.

## Device roles

- `HOST`
- `CAMERA`
- `SCREEN`
- `AUDIO`
- `MODERATOR`
- `OPERATOR`
- `VIEWER_MONITOR`

## Pairing

Use a short-lived pairing code or QR flow.

Rules:

- Pairing codes expire.
- A paired device receives only the permissions granted to its role.
- Revocation is immediate.
- Device credentials are not permanent API keys.

## Session topology

```text
Live Session
 |-- Device A: Host camera
 |-- Device B: Secondary camera
 |-- Device C: Screen source
 |-- Device D: Moderator
 |-- Device E: Control operator
```

## Phase 4 behavior

Initially, multiple devices can contribute independently and be selected/managed by an operator. Full multi-source composition and automatic switching belongs in the professional studio phase.

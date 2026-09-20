# Phase 2 — Multi-Platform Distribution

## Objective

Broadcast the same Live Session to external platforms without changing the creator workflow.

## Scope

- Destination model.
- Provider adapter interface.
- YouTube adapter.
- Facebook adapter.
- TikTok adapter, subject to current API/publishing eligibility.
- Custom RTMP.
- Destination status and retries.
- Credential storage and rotation.
- Destination management UI.

## Acceptance criteria

- Core stream can remain LIVE if one destination fails.
- Destination status is visible independently.
- Credentials never appear in frontend responses.
- Transient publishing failures retry.
- Provider-specific errors are normalized into stable internal error codes.

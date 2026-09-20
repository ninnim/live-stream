# 10 — Database Design

## Core tables

### users
- id
- email
- display_name
- status
- created_at
- updated_at

### workspaces
- id
- name
- owner_user_id
- status
- created_at

### workspace_members
- workspace_id
- user_id
- role_id
- created_at

### live_sessions
- id
- workspace_id
- title
- status
- visibility
- recording_enabled
- started_at
- ended_at
- created_by
- version
- created_at
- updated_at

### live_devices
- id
- live_session_id
- device_name
- role
- status
- last_seen_at
- paired_at
- revoked_at

### live_sources
- id
- live_session_id
- device_id
- type
- status
- metadata_json

### destinations
- id
- live_session_id
- provider
- credential_ref
- config_json
- status
- last_error
- started_at
- ended_at

### recordings
- id
- live_session_id
- storage_key
- status
- duration_seconds
- size_bytes
- started_at
- ended_at

### audit_logs
- id
- workspace_id
- actor_user_id
- action
- entity_type
- entity_id
- metadata_json
- created_at

## Data rules

- Use UUID/ULID consistently.
- Prefer UTC timestamps.
- Do not store secrets in ordinary config JSON.
- Index session lookup by workspace and status.
- Index device heartbeat by session and last_seen.
- Separate long-lived analytics/event data from transactional tables when volume grows.

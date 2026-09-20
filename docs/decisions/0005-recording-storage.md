# ADR 0005 — Phase 1 recording storage

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

`docs/03-streaming-engine.md` requires that recording be associated with the Live Session and use
durable object storage, with metadata covering session id, recording id, storage key, duration,
start/end timestamps, media format, size, and status.

`ai/architecture-rules.md` states that recording belongs to the Live Session but storage is external
to the transactional database.

The Phase 1 scope is "basic recording architecture" — the model and finalization, not a media
pipeline.

## Decision

Split responsibilities:

- **The media gateway writes the media.** MediaMTX records fMP4 segments to a volume.
- **The API owns the metadata.** A `recordings` row tracks status, storage key, duration, size, and
  segment count through `PENDING → RECORDING → FINALIZING → READY | FAILED`.
- **`IRecordingStore` is the storage boundary.** Phase 1 ships `FileSystemRecordingStore`, which
  inspects the shared volume when a session ends. An S3-compatible implementation replaces it
  without touching session logic.

## Why not S3 in Phase 1

Uploading segments to object storage is a media-worker responsibility: it needs a queue, a retry
policy, partial-upload handling, and lifecycle rules. That is real work with real failure modes, and
none of it changes whether a creator can broadcast — which is what Phase 1 must prove. Building it
now would mean shipping an untested upload pipeline alongside an untested streaming path.

The filesystem implementation is a genuine implementation, not a stub: it reports real sizes,
timestamps, and segment counts from real files, and its failure paths are tested.

## Notable details

**A recording failure never fails the broadcast.** `RecordingService.FinalizeAsync` catches storage
errors, marks the recording `FAILED` with a reason, and lets the session reach `ENDED` normally.
This follows `ai/coding-rules.md` rule 16.

**"No media captured" is distinguished from "storage unreadable".** `InspectAsync` returns `null`
for the former and throws for the latter. Both mark the recording `FAILED`, but with different
reasons, so an operator can tell a silent broadcaster apart from a broken volume.

**Storage keys are validated before touching the filesystem.** Keys derive from our own generated
path names, but they still reach a path join, so anything outside `[a-z0-9_-]` is rejected rather
than trusted.

## Consequences

- Recordings live on a volume in Phase 1 and are not durable against host loss. Acceptable for an
  MVP; it must change before the recording completion rate becomes a tracked reliability target.
- Retention is unlimited. Lifecycle policy belongs with the object store that replaces this.
- Playback of past recordings is not exposed yet — the metadata is available through
  `GET /api/v1/live-sessions/{id}/recordings`, but serving the media is a later phase.

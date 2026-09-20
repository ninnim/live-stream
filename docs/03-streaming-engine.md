# 03 — Streaming Engine

## Purpose

Provide reliable ingest, processing, recording, playback, and external distribution without coupling business logic to one vendor.

## Protocol strategy

### Native web/mobile contribution
Prefer WebRTC-based contribution when low-latency interactive capture is required. A media gateway converts/contributes the stream into the internal media pipeline.

### External encoders
Support RTMP and SRT as input protocols.

### Viewer delivery
Use HLS/LL-HLS for large-scale delivery where appropriate. Use WebRTC when interactive low-latency delivery is required.

## Media pipeline

```text
Source
 -> Ingest
 -> Validate
 -> Normalize
 -> Transcode / Package
 -> Record
 -> Distribute
 -> Observe
```

## Quality profiles

Use configurable profiles rather than hardcoded device-specific behavior. A starting ladder may include 1080p, 720p, and 480p with frame-rate and bitrate limits defined by the deployment.

## Adaptive behavior

- Detect sustained network degradation.
- Reduce quality before disconnecting when possible.
- Restore quality gradually after recovery.
- Avoid rapid quality oscillation using hysteresis.

## Reconnect

- Distinguish source failure, network failure, server failure, and destination failure.
- Keep session state alive for a bounded recovery window.
- Do not terminate a live session because one external destination failed.

## Recording

Recording must be associated with the Live Session and use durable object storage.

Minimum metadata:
- session ID
- recording ID
- storage key
- duration
- start/end timestamp
- media format
- size
- status

## Media security

- Never expose permanent ingest secrets to clients.
- Use short-lived, scoped credentials wherever possible.
- Validate session ownership before issuing ingest tokens.
- Rotate destination credentials according to provider requirements.

# 12 — Observability & Reliability

## Required telemetry

Metrics:
- active live sessions
- session start success rate
- ingest connection success rate
- reconnect count
- bitrate
- dropped frames
- packet loss where measurable
- end-to-end latency
- destination success/failure
- recording success/failure
- API latency/error rate

Logs:
- structured JSON
- correlation ID
- tenant/session ID where safe
- no secrets

Traces:
- create session
- start session
- issue credential
- media handoff
- recording finalization
- destination publish

## Reliability principles

- Control plane should remain usable if one destination fails.
- Media workers must retry transient failures with bounded backoff.
- Destination publishing must be independently restartable.
- Reconnect logic must use bounded recovery windows.
- Prefer idempotent jobs.

## Alerts

Alert on:
- sudden stream-start failure spikes
- sustained ingest disconnects
- recording failures
- destination adapter failure spikes
- high API error rates
- media worker saturation

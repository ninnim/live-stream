# 06 — Multi-Platform Distribution

## Destination abstraction

Use one internal contract for all destinations.

```text
Destination
- id
- sessionId
- provider
- displayName
- endpointConfig
- credentialRef
- status
- lastError
- startedAt
- stoppedAt
```

Providers should implement a common interface such as:

```text
connect(destination)
validate(destination)
start(destination, session)
stop(destination)
getStatus(destination)
refreshCredential(destination)
```

## Destination failures

External destination failure is isolated from the core live session.

Example:

```text
Your Platform  LIVE
YouTube         LIVE
Facebook        ERROR
TikTok          LIVE
```

Do not stop the whole session because Facebook is unavailable.

## Provider integration policy

Provider APIs and publishing requirements vary over time. Keep provider adapters isolated and configurable. Never hardcode provider-specific assumptions into the session domain model.

## Initial destinations

- Your platform
- YouTube
- Facebook
- TikTok
- Custom RTMP

Add new providers behind the same destination interface.

# 04 — Native Broadcasting

## Core decision

A normal creator must broadcast without OBS.

## Web broadcaster

The web app should:

- request camera/microphone permission;
- preview local capture;
- enumerate available devices where browser permissions allow;
- select camera and microphone;
- show network health;
- create a short-lived ingest credential;
- start contribution;
- send control/state events to the backend;
- recover from temporary connection loss;
- stop cleanly and finalize recording.

## Browser capability detection

Feature-detect camera, microphone, screen-sharing, codec, and browser support. Do not assume every browser exposes every feature.

## Mobile broadcaster

The mobile client owns capture, local permission handling, lifecycle rules, reconnect strategy, and safe transitions when the app loses focus or connectivity.

## External encoder

OBS is supported only as an optional producer. The external encoder connects to the same session through an ingest endpoint and scoped stream credential.

## UX states

- Camera permission required
- Microphone permission required
- Preparing
- Connecting
- Live
- Reconnecting
- Degraded quality
- Stopping
- Ended
- Failed

## Safety behavior

The client must not show "Live" until the backend confirms the session is accepted and the media pipeline has reached the intended state.

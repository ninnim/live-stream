# Phase 1 Coding-Agent Prompt

You are implementing Phase 1 of a native-first live-streaming platform.

Read these files first:

- `MASTER_BLUEPRINT.md`
- `ai/coding-rules.md`
- `ai/architecture-rules.md`
- `ai/implementation-workflow.md`
- `ai/definition-of-done.md`
- `implementation/phase-1-native-web-broadcasting.md`
- `docs/01-product-requirements.md`
- `docs/02-system-architecture.md`
- `docs/03-streaming-engine.md`
- `docs/04-native-broadcasting.md`
- `docs/09-api-specification.md`
- `docs/10-database-design.md`
- `docs/12-observability-and-reliability.md`
- `docs/14-testing.md`

## Mission

Implement only Phase 1: native web broadcasting without OBS.

## Critical product rule

The creator must be able to broadcast directly from the web application. OBS is optional and must not be required anywhere in the normal Phase 1 flow.

## Required behavior

1. Inspect the existing repository and identify frontend, backend, database, auth, realtime, and deployment patterns.
2. Do not rewrite unrelated systems.
3. Implement the Live Session domain and server-owned state machine.
4. Add database schema/migrations for the Phase 1 model.
5. Add REST endpoints for create/start/stop/session read and scoped broadcaster credential issuance.
6. Add SignalR events for authoritative live state and health changes.
7. Implement the native web broadcaster UI.
8. Implement local camera/microphone preview.
9. Integrate the agreed media ingest path behind a clear backend/media abstraction.
10. Implement viewer playback for the Phase 1 media path.
11. Implement recording metadata/finalization.
12. Implement recoverable reconnect behavior.
13. Add structured logs and metrics for session lifecycle and media health.
14. Add automated tests for state transitions, authorization, API behavior, reconnect behavior, and the end-to-end happy path that can be automated in the repository.

## Constraints

- Do not implement YouTube/Facebook/TikTok.
- Do not implement mobile apps.
- Do not implement AI.
- Do not implement advanced scenes or multi-camera composition.
- Do not introduce permanent client-visible streaming secrets.
- Do not claim success without running tests/builds.

## Working method

Start by inspecting the repository. Before editing, briefly identify:

- current architecture;
- reusable components;
- exact files to add/change;
- risks or missing infrastructure.

Then implement in small vertical slices.

After each major slice, run the relevant tests/build.

At completion, report:

- summary of implementation;
- files changed;
- migrations;
- API endpoints;
- SignalR events;
- environment variables;
- setup/run instructions;
- tests executed and results;
- known limitations;
- anything intentionally deferred to Phase 2+.

# AI Implementation Workflow

## Before coding

1. Read `MASTER_BLUEPRINT.md`.
2. Read this phase's document.
3. Inspect the repository structure and existing architecture.
4. Identify what already exists.
5. Produce a concise implementation map before editing.

## During coding

1. Implement the smallest vertical slice first.
2. Keep changes scoped to the phase.
3. Run formatting/static checks.
4. Run unit tests.
5. Run integration tests when available.
6. Verify API contracts.
7. Verify realtime event behavior.
8. Verify error paths.

## After coding

1. Run the complete relevant test suite.
2. Run the application locally when possible.
3. Verify migrations from a clean database.
4. Verify environment configuration.
5. Update docs.
6. Report known limitations honestly.

## Never

- start by generating thousands of lines without repository inspection;
- mark TODOs as completed;
- replace real integration tests with mocks only;
- hide failed commands;
- silently skip media/runtime concerns.

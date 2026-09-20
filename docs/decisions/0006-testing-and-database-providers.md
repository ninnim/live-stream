# ADR 0006 — Testing strategy and database providers

**Status:** Accepted
**Date:** 2026-08-30
**Phase:** 1 — Native Web Broadcasting

## Context

`docs/14-testing.md` requires unit tests for state transitions and authorization, integration tests
for API + database and SignalR, and an end-to-end test of the critical journey.

`ai/implementation-workflow.md` forbids replacing real integration tests with mocks only, and forbids
claiming a feature works without running the relevant commands.

Production uses PostgreSQL. A test suite that needs a database container on every developer machine
gets skipped, and a suite that uses EF Core's in-memory provider does not test relational behaviour
at all — no foreign keys, no unique constraints, no concurrency conflicts.

## Decision

Three tiers, each answering a different question.

### 1. `LiveStream.UnitTests` — does the business logic hold?

Pure logic (state machine, permissions, credential expiry, backoff) plus service-level tests against
**SQLite in memory**. SQLite is a real relational engine: foreign keys, unique indexes, and
optimistic-concurrency failures all behave correctly.

Runs anywhere, in about a second.

### 2. `LiveStream.IntegrationTests` — does the API behave?

The real application hosted by `WebApplicationFactory`: real routing, authentication, authorization,
model binding, exception handling, rate limiting, and DI, over SQLite. Only the media gateway and
recording store are substituted, because the gateway is a separate process.

This tier covers the full Phase 1 journey — create → prepare → credential → start → LIVE → health →
stop → ENDED → recording finalized — plus authorization, session isolation, and the media auth
callback.

### 3. `LiveStream.DatabaseTests` — is the production schema right?

Real PostgreSQL via Testcontainers, applying the committed EF migrations. This is the only tier that
can prove migrations apply cleanly, are idempotent, and produce a schema the model round-trips
against.

**When Docker is absent these tests report as skipped, never as passed.** `RequiresDockerFactAttribute`
sets a skip reason so the output states plainly that the schema was not verified.

## Making one model run on both providers

The EF model uses no provider-specific column types. Two accommodations were needed:

- **`DateTimeOffset` ordering.** SQLite has no native date type and cannot `ORDER BY` a
  `DateTimeOffset`. `AppDbContext.ConfigureConventions` applies EF's `DateTimeOffsetToBinaryConverter`
  — which encodes to a sortable integer — **only** when the provider is SQLite. PostgreSQL orders
  `timestamptz` natively and is untouched.
- **Provider detection by name.** `Database.ProviderName` is inspected rather than calling
  `IsSqlite()`, which would require referencing the SQLite provider package from production code.

## Model-vs-migration drift

Checked in CI with `dotnet ef migrations has-pending-model-changes`, the supported tool for it. An
in-test version was attempted but required EF internals, which is not worth the coupling.

## Consequences

- The default `dotnet test` run passes on any machine and genuinely exercises the API.
- The schema guarantee depends on Docker being present. CI has it; the Phase 1 report states plainly
  where it was and was not exercised.
- SQLite and PostgreSQL can drift in ways tier 3 catches only in CI. The absence of provider-specific
  column types keeps that surface small.
